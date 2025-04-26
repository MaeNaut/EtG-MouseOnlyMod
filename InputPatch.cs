using Alexandria.cAPI;
using Brave.BulletScript;
using Dungeonator;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using System.Collections.Generic;
using System;
using Alexandria.ItemAPI;

namespace MouseOnlyMod
{
    [HarmonyPatch(typeof(PlayerController), "Update")]
    public class InputPatch
    {
        #region Properties
        private static PlayerController CurrentPlayer { get; set; }
        private static RoomHandler CurrentRoom => CurrentPlayer.CurrentRoom;
        private static Vector2 PlayerPosition => CurrentPlayer.CenterPosition;
        private static Vector2 PlayerVelocity
        {
            get => CurrentPlayer.specRigidbody.Velocity;
            set => CurrentPlayer.specRigidbody.Velocity = value;
        }
        #endregion Properties

        // It is a timer to prevent the player and camera from trembling.
        private static Vector2? _lastSlideDirection = null;
        private static float _slideCooldownTimer = 0f;

        // Combat area itself and its debug variables.
        private static Rect combatArea;
        private static GameObject[] combatBorders = new GameObject[4];
        private static bool Debug_ShowCombatAreaBorders = false;

        // The main method the AI behavior.
        static void Postfix(PlayerController __instance)
        {
            // Update the current player instance.
            CurrentPlayer = __instance;

            // Is the mouse-only mod disabled?
            if (!Plugin.MouseOnlyEnabled || CurrentPlayer == null || CurrentRoom == null)
                return;

            // If the player is in case where any input is not accepted, skip the whole Postfix method.
            if (!CurrentPlayer.AcceptingAnyInput)
            {
                PlayerVelocity = Vector2.zero;
                return;
            }

            // Update the combat area of the current room.
            combatArea = CalculateCombatArea();

            UpdateSlideCooldown();

            // Find and store the most dangerous bullet in the scene.
            List<Projectile> dangerousBullets = DetectDangerousBullets();

            // Enable mouse input if the player is not rolling.
            if (!CurrentPlayer.IsDodgeRolling)
            {
                // During combat, and a dangerous projectile is found.
                if (dangerousBullets != null)
                {
                    // If the bullet is expected to hit next frame, roll to the safe position.
                    if (WillBulletHitPlayerNextFrame(dangerousBullets))
                    {
                        SafeDodgeRoll(dangerousBullets);
                    }
                    // If not, avoid from the bullet.
                    else
                    {
                        HandleBulletAvoidance(dangerousBullets);
                    }
                }
                // During combat, and keep the distance from the enemies.
                else if (CurrentPlayer.IsInCombat)
                {
                    HandleEnemySpacing();
                }
                // Out of combat, so just follow the cursor.
                else
                {
                    HandleMouseFollow();
                }
            }

            // In general, dodge roll when mouse left button is clicked.
            ManualDodgeRoll();

            if (CurrentPlayer.IsInCombat && Debug_ShowCombatAreaBorders)
            {
                UpdateCombatAreaBorders();
            }
            else
            {
                DestroyCombatAreaBorders();
            }
        }


        // Slide cooldown helper function
        public static void UpdateSlideCooldown()
        {
            if (_slideCooldownTimer > 0.0f)
            {
                _slideCooldownTimer -= BraveTime.DeltaTime;
            }
            else
            {
                _slideCooldownTimer = 0.0f;
                _lastSlideDirection = null;
            }
        }

        // Allow the player to dodge roll toward cursor by right-clicking.
        public static void ManualDodgeRoll()
        {
            if (Input.GetMouseButtonDown(1) && !CurrentPlayer.IsDodgeRolling)
            {
                Vector2 dodgeDir = (Camera.main.ScreenToWorldPoint(Input.mousePosition).XY() - PlayerPosition).normalized;
                if (dodgeDir.magnitude < 0.1f)
                    dodgeDir = Vector2.right;

                CurrentPlayer.StartDodgeRoll(dodgeDir);
            }
        }

        // Calculate the combat area of the current room.
        public static Rect CalculateCombatArea()
        {
            RoomHandler room = CurrentPlayer.CurrentRoom;
            if (room == null)
                return new Rect();

            Vector2 min = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 max = new Vector2(float.MinValue, float.MinValue);

            // Iterate through all cells in the room and find the min/max bounds.
            foreach (IntVector2 cellPos in room.Cells)
            {
                var cell = GameManager.Instance.Dungeon.data[cellPos];
                if (cell != null && cell.type == CellType.FLOOR && !cell.isExitCell && !cell.isDoorFrameCell)
                {
                    // Perfect offset to make the combat area more accurate.
                    Vector2 worldPos = cellPos.ToVector2() + new Vector2(0.5f, 1.3f);
                    min = Vector2.Min(min, worldPos);
                    max = Vector2.Max(max, worldPos);
                }
            }

            // Slightly enlarge bounds to make the combat area more accurate.
            min -= Vector2.one * 0.15f;
            max += Vector2.one * 0.15f;

            return new Rect(min, max - min);
        }

        // Try to keep the player in the combat area.
        public static void BindToCombatArea()
        {
            // If the player is out of the combat area during combat, correct their velocity.
            if (CurrentPlayer.IsInCombat && !combatArea.Contains(PlayerPosition))
            {
                Vector2 correctedDir = PlayerVelocity;

                // Handle X axis
                if (PlayerPosition.x < combatArea.xMin && correctedDir.x < 0f)
                    correctedDir.x = 0f; // Moving left when need to move right, cancel X
                if (PlayerPosition.x > combatArea.xMax && correctedDir.x > 0f)
                    correctedDir.x = 0f; // Moving right when need to move left, cancel X

                // Handle Y axis
                if (PlayerPosition.y < combatArea.yMin && correctedDir.y < 0f)
                    correctedDir.y = 0f; // Moving down when need to move up, cancel Y
                if (PlayerPosition.y > combatArea.yMax && correctedDir.y > 0f)
                    correctedDir.y = 0f; // Moving up when need to move down, cancel Y

                PlayerVelocity = correctedDir;
            }
        }


        // Find the dangerous bullets in the scene.
        public static List<Projectile> DetectDangerousBullets()
        {
            List<Projectile> dangerousBullets = new List<Projectile>();
            
            float dangerRadius = 2.5f;

            // Always check for the incoming bullets.
            foreach (Projectile proj in StaticReferenceManager.AllProjectiles)
            {
                if (proj == null || proj.Owner is PlayerController)
                    continue;

                Vector2 bulletPos = proj.sprite.WorldCenter;
                Vector2 toPlayer = PlayerPosition - bulletPos;
                float distance = toPlayer.magnitude;
                float angleDiff = Vector2.Angle(toPlayer, proj.Direction);

                // A bullet is considered dangerous if it is close to and moving toward the player
                if (distance < dangerRadius && angleDiff < 60f)
                {
                    dangerousBullets.Add(proj);
                }
            }

            // Return the list of dangerous bullets or null.
            return dangerousBullets.Count > 0 ? dangerousBullets : null;
        }


        // Avoid the dangerous bullets found from DetectDangerousBullet.
        public static void HandleBulletAvoidance(List<Projectile> dangerousBullets)
        {
            // Calculate the direction that is the average direction from the dangerous bullets
            Vector2 combinedBulletDir = Vector2.zero;
            foreach (Projectile proj in dangerousBullets)
            {
                combinedBulletDir += proj.Direction;
            }
            combinedBulletDir.Normalize();

            // Perpendicular directions
            Vector2 perp1 = new Vector2(-combinedBulletDir.y, combinedBulletDir.x);
            Vector2 perp2 = new Vector2(combinedBulletDir.y, -combinedBulletDir.x);

            // Blend perpendicular and backward directions to creat two diagonal directions.
            float retreatWeight = 0.7f; // how much backward to blend
            Vector2 diag1 = (perp1 + combinedBulletDir * retreatWeight).normalized;
            Vector2 diag2 = (perp2 + combinedBulletDir * retreatWeight).normalized;

            // Choose the one that moves the player further from the bullet
            Vector2 playerDir = PlayerVelocity.normalized;
            float a1 = Vector2.Angle(playerDir, diag1);
            float a2 = Vector2.Angle(playerDir, diag2);
            Vector2 finalDirection = a1 < a2 ? diag1 : diag2;

            // If the player is out of combat and IncreaseSpeedOutOfCombat option is turned on, boost the speed.
            float speed = CurrentPlayer.stats.MovementSpeed;
            if (!CurrentPlayer.IsInCombat && GameManager.Options.IncreaseSpeedOutOfCombat)
                speed *= 1.5f;

            TryMove(finalDirection, speed);
        }

        // Keep the best distance from the nearest enemy in the scene.
        public static void HandleEnemySpacing()
        {
            float speed = CurrentPlayer.stats.MovementSpeed;
            float idealDistance = 7.0f;
            float distanceThreshold = 0.5f;

            // If there is a boss in current room, increase the distance.
            foreach (AIActor enemy in CurrentRoom.activeEnemies)
            {
                if (enemy.healthHaver.IsBoss)
                {
                    idealDistance = 10.0f;
                    distanceThreshold = 1.0f;
                    break;
                }
            }

            // Get the nearest enemy in the current room.
            AIActor nearestEnemy = CurrentRoom.GetNearestEnemy(PlayerPosition, out float distToEnemy, true, true);

            if (nearestEnemy != null)
            {
                Vector2 enemyPos = nearestEnemy.CenterPosition;
                Debug.DrawLine(new Vector3(PlayerPosition.x, PlayerPosition.y, 0f), new Vector3(enemyPos.x, enemyPos.y, 0f), Color.red);
                float distance = Vector2.Distance(PlayerPosition, enemyPos);

                // Dodge roll if the enemy is too close.
                if (distance < 2.0f)
                {
                    SafeDodgeRoll();
                    return;
                }
                // Move away if the enemy is closer than the ideal distance.
                else if (distance < idealDistance - distanceThreshold)
                {
                    Vector2 directionAway = (PlayerPosition - enemyPos).normalized;

                    TryMove(directionAway, speed);
                }
                // Move toward if the enemy is further than the ideal distance.
                else if (distance > idealDistance + distanceThreshold)
                {
                    Vector2 directionToward = (enemyPos - PlayerPosition).normalized;

                    TryMove(directionToward, speed);
                }
                // No need to move once the enemy is within the ideal distance.
                else
                {
                    PlayerVelocity = Vector2.zero;
                }
            }
        }

        // Make the player character follow the mouse cursor.
        public static void HandleMouseFollow()
        {
            //Vector3 mouseWorldPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            //mouseWorldPos.z = 0f;

            Vector2 mousePos = Camera.main.ScreenToWorldPoint(Input.mousePosition).XY();
            float distance = Vector2.Distance(PlayerPosition, mousePos);
            float speed = CurrentPlayer.stats.MovementSpeed;

            // If the cursor is some distance away from the character, follow it.
            if (distance > 1.5f)
            {
                Vector2 direction = (mousePos - PlayerPosition).normalized;

                if (GameManager.Options.IncreaseSpeedOutOfCombat)
                    speed *= 1.5f;

                TryMove(direction, speed);
            }
            // If not, stop.
            else
            {
                PlayerVelocity = Vector2.zero;
            }
        }


        // Validate if the moving direction is not blocked by any obstacles.
        public static bool IsDirectionClear(Vector2 direction, float distance = 0.5f)
        {
            // Must offset, or the method will return false for left/right directions
            // when the player is right below the wallss.
            Vector2 offsetOrigin = PlayerPosition + new Vector2(0f, -0.5f);

            // Check every ~0.5 units
            Vector2 step = direction.normalized * 0.5f;

            // Number of steps, but at least 1 step even if distance is 0.0f.
            int steps = Mathf.Max(1, Mathf.CeilToInt(distance / 0.5f));

            for (int i = 1; i <= steps; i++)
            {
                Vector2 pos = offsetOrigin + step * i;
                IntVector2 cell = pos.ToIntVector2(VectorConversions.Floor);

                // If data are invalid, return false.
                if (!GameManager.Instance.Dungeon.data.CheckInBoundsAndValid(cell))
                    return false;
                CellData cellData = GameManager.Instance.Dungeon.data[cell];
                if (cellData == null)
                    return false;

                // If the direction contains a wall, pit, or door, return false.
                bool isFloor = cellData.type == CellType.FLOOR;
                bool isDoor = cellData.isDoorFrameCell;
                if (!isFloor || (isDoor && CurrentPlayer.IsInCombat))
                    return false;
            }

            return true;
        }

        // Try moving the player.
        public static void TryMove(Vector2 direction, float speed)
        {
            // If the direction is valid, move.
            if (IsDirectionClear(direction))
            {
                PlayerVelocity = direction * speed;
            }
            // If the direction is containing any obstacles, try to slide through them.
            else if (CurrentPlayer.IsInCombat)
            {
                Vector2? slide = FindSlideAlongWallDirection(direction);
                if (slide.HasValue)
                {
                    PlayerVelocity = slide.Value * speed;
                    _lastSlideDirection = slide.Value;
                    _slideCooldownTimer = 0.1f;
                }
                else
                {
                    PlayerVelocity = Vector2.zero;
                }
            }
            else
            {
                PlayerVelocity = Vector2.zero;
            }
            // Always bind the player to the combat area during combat.
            BindToCombatArea();
        }

        // Want to move but faced an obstacle? Try to slide along it.
        public static Vector2? FindSlideAlongWallDirection(Vector2 direction, float checkDistance = 0.5f)
        {
            Vector2 perpLeft = new Vector2(-direction.y, direction.x);   // 90° left
            Vector2 perpRight = new Vector2(direction.y, -direction.x);  // 90° right

            if (_lastSlideDirection.HasValue && IsDirectionClear(_lastSlideDirection.Value, checkDistance))
            {
                return _lastSlideDirection;
            }

            if (IsDirectionClear(perpLeft, checkDistance))
            {
                _lastSlideDirection = perpLeft;
                _slideCooldownTimer = 0.1f;
                return perpLeft;
            }

            if (IsDirectionClear(perpRight, checkDistance))
            {
                _lastSlideDirection = perpRight;
                _slideCooldownTimer = 0.1f;
                return perpRight;
            }

            return null;
        }


        // Literally, check AABB collision of given two objects.
        public static bool CheckAABBCollision(PixelCollider a, Vector2 aPos, PixelCollider b, Vector2 bPos, float padding = 5.0f)
        {
            float aMinX = aPos.x + a.MinX - padding;
            float aMaxX = aPos.x + a.MaxX + padding;
            float aMinY = aPos.y + a.MinY - padding;
            float aMaxY = aPos.y + a.MaxY + padding;

            float bMinX = bPos.x + b.MinX - padding;
            float bMaxX = bPos.x + b.MaxX + padding;
            float bMinY = bPos.y + b.MinY - padding;
            float bMaxY = bPos.y + b.MaxY + padding;

            return (aMinX <= bMaxX && aMaxX >= bMinX && aMinY <= bMaxY && aMaxY >= bMinY);
        }

        // Check if the player will get hit by the dangerous bullet next frame.
        public static bool WillBulletHitPlayerNextFrame(List<Projectile> dangerousBullets)
        {
            // Calculate the player's position in next frame.
            Vector2 nextPlayerPos = PlayerPosition + PlayerVelocity * BraveTime.DeltaTime;

            // Get the pixel collider of the player.
            PixelCollider playerCollider = CurrentPlayer.specRigidbody?.HitboxPixelCollider;
            if (playerCollider == null)
                return false;

            foreach (Projectile proj in dangerousBullets)
            {
                // Calculate the dangerous bullet's position in next frame.
                Vector2 nextBulletPos = proj.sprite.WorldCenter + proj.Direction.normalized * proj.Speed * BraveTime.DeltaTime;

                // Get the pixel collider of the bullet.
                PixelCollider bulletCollider = proj.specRigidbody?.PrimaryPixelCollider;
                if (bulletCollider == null)
                    return false;

                // Calculate the collision using future positions and their collider information.
                return CheckAABBCollision(bulletCollider, nextBulletPos, playerCollider, nextPlayerPos);
            }

            return false;
        }

        // Is the player about to get hit next frame? Find the best direction to roll.
        public static void SafeDodgeRoll(List<Projectile> dangerousBullets = null)
        {
            Vector2 bestDirection = Vector2.zero;
            float bestScore = float.MinValue;

            Vector2[] testDirs = new Vector2[]
            {
                Vector2.left, Vector2.right, Vector2.up, Vector2.down,          // cardinal
                new Vector2(-1, 1).normalized, new Vector2(1, 1).normalized,    // diagonal
                new Vector2(-1, -1).normalized, new Vector2(1, -1).normalized   // total 8 directions
            };

            // Calculate the direction that is the average direction to the enemies within 10 units
            Vector2 threatVector = Vector2.zero;
            int threatCount = 0;
            const float threatRange = 10.0f;
            foreach (AIActor enemy in StaticReferenceManager.AllEnemies)
            {
                if (enemy == null || !enemy.healthHaver || enemy.healthHaver.IsDead)
                    continue;

                float dist = Vector2.Distance(PlayerPosition, enemy.CenterPosition);
                if (dist <= threatRange)
                {
                    threatVector += (enemy.CenterPosition - PlayerPosition).normalized;
                    threatCount++;
                }
            }
            Vector2 combinedThreatDir = threatCount > 0 ? threatVector.normalized : Vector2.zero;


            foreach (Vector2 dir in testDirs)
            {
                // Dodge roll in Enter the Gungeon moves the player about 5.5 units.
                const float ROLL_DISTANCE = 5.5f;
                const float ROLL_TIME = 0.7f;

                // The expected destination of the roll.
                Vector2 rollTarget = PlayerPosition + dir * ROLL_DISTANCE;

                // Skip if the direction contains obstacles or is out of combat area.
                if (!IsDirectionClear(dir, ROLL_DISTANCE) || !combatArea.Contains(rollTarget))
                    continue;

                // 1. Away from the enemies score (further is safer)
                float awayFromEnemyScore = 0.0f;
                if (combinedThreatDir != Vector2.zero)
                {
                    awayFromEnemyScore = Vector2.Angle(dir, combinedThreatDir); // max = 180 when rolling away
                }


                // Iterate through all enemy bullets in the room.
                float bulletDistanceScore = float.MaxValue;
                float bulletCountDeduction = 0;
                foreach (Projectile proj in StaticReferenceManager.AllProjectiles)
                {
                    if (proj.Owner is PlayerController)
                        continue;

                    // The expected destination of the bullet.
                    Vector2 futureBulletPos = proj.specRigidbody.UnitCenter + proj.Direction.normalized * proj.Speed * ROLL_TIME;

                    // Calculate the distance between each destination of the roll and bullet.
                    float dist = Vector2.Distance(rollTarget, futureBulletPos);

                    // 2. Nearest bullet distance score (further is safer)
                    if (dist < bulletDistanceScore)
                        bulletDistanceScore = dist;

                    // 3. Bullet count nearby destination score (less is safer)
                    if (dist < 5.0f)
                        bulletCountDeduction++;
                }


                float perpendicularScore = 0.0f;
                if (dangerousBullets != null)
                {
                    foreach (Projectile proj in dangerousBullets)
                    {
                        float angleDiff = Vector2.Angle(dir, proj.Direction.normalized);

                        // 4. Bullet alignment score (prefer perpendicular)
                        perpendicularScore += 90f - Mathf.Abs(90f - angleDiff);
                    }
                    perpendicularScore /= Mathf.Max(1, dangerousBullets.Count);
                }



                // Combine all scores
                float score = awayFromEnemyScore * 0.2f + bulletDistanceScore * 2.0f + perpendicularScore * 0.2f - Mathf.Min(bulletCountDeduction * 2.0f, 5.0f);
                //ETGModConsole.Log("[DEBUG] Direction = (" + dir.x + ", " + dir.y + ") Score = " + awayFromEnemyScore * 0.3f + " + " + bulletDistanceScore * 1.5f + " + " + perpendicularScore * 0.3f + " - " + Mathf.Min(bulletCountDeduction * 2.0f, 5.0f) + " = " + score);

                // Update the best score
                if (score > bestScore)
                {
                    bestScore = score;
                    bestDirection = dir;
                }
            }

            // Dodge roll toward the best direction
            if (!CurrentPlayer.IsDodgeRolling)
            {
                CurrentPlayer.StartDodgeRoll(bestDirection);
            }
            //ETGModConsole.Log("[DEBUG] End of SafeDodgeRoll");
        }


        #region DebugMethods
        #region CombatAreaBorders
        // Create the combat area border with red color.
        public static void CreateCombatAreaBorders()
        {
            if (combatBorders[0] != null) return; // Already created

            for (int i = 0; i < 4; i++)
            {
                combatBorders[i] = GameObject.CreatePrimitive(PrimitiveType.Quad);
                combatBorders[i].GetComponent<Renderer>().material.color = Color.red;
                combatBorders[i].GetComponent<Renderer>().sortingOrder = 1000;
            }
        }

        // Update the combat area borders with perfect position and scale.
        public static void UpdateCombatAreaBorders()
        {
            if (!Debug_ShowCombatAreaBorders) return;
            if (combatBorders[0] == null) CreateCombatAreaBorders();

            float thickness = 0.2f;

            // Top
            combatBorders[0].transform.position = new Vector3(combatArea.center.x, combatArea.yMax, 1f);
            combatBorders[0].transform.localScale = new Vector3(combatArea.width, thickness, 1f);

            // Bottom
            combatBorders[1].transform.position = new Vector3(combatArea.center.x, combatArea.yMin, 1f);
            combatBorders[1].transform.localScale = new Vector3(combatArea.width, thickness, 1f);

            // Left
            combatBorders[2].transform.position = new Vector3(combatArea.xMin, combatArea.center.y, 1f);
            combatBorders[2].transform.localScale = new Vector3(thickness, combatArea.height, 1f);

            // Right
            combatBorders[3].transform.position = new Vector3(combatArea.xMax, combatArea.center.y, 1f);
            combatBorders[3].transform.localScale = new Vector3(thickness, combatArea.height, 1f);
        }

        // Destroy the combat area borders.
        public static void DestroyCombatAreaBorders()
        {
            for (int i = 0; i < combatBorders.Length; i++)
            {
                if (combatBorders[i] != null)
                {
                    UnityEngine.Object.Destroy(combatBorders[i]);
                    combatBorders[i] = null;
                }
            }
        }
        #endregion CombatAreaBorders
        #endregion DebugMethods
    }
}
