using Alexandria.cAPI;
using Brave.BulletScript;
using Dungeonator;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;
using System.Collections.Generic;

namespace MouseOnlyMod
{
    [HarmonyPatch(typeof(PlayerController), "Update")]
    public class InputPatch
    {
        // It is a timer to prevent the player and camera from trembling.
        private static Vector2? _lastSlideDirection = null;
        private static float _slideCooldownTimer = 0f;

        static void Postfix(PlayerController __instance)
        {
            // Is the mouse-only mod disabled?
            if (!Plugin.MouseOnlyEnabled || __instance == null || __instance.CurrentRoom == null) return;

            // If the player is in case where any input is not accepted, skip the whole Postfix method.
            if (!__instance.AcceptingAnyInput)
            {
                __instance.specRigidbody.Velocity = Vector2.zero;
                return;
            }

            // Make it simple.
            Vector2 playerPos = __instance.CenterPosition;

            _slideCooldownTimer -= BraveTime.DeltaTime;
            if (_slideCooldownTimer < 0f)
            {
                _slideCooldownTimer = 0f;
                _lastSlideDirection = null;
            }

            // Find and store the most dangerous bullet in the scene.
            List<Projectile> dangerousBullets = DetectDangerousBullets(__instance);

            // Enable mouse input if the player is not rolling.
            if (!__instance.IsDodgeRolling)
            {
                // During combat, and a dangerous projectile is found.
                if (dangerousBullets != null)
                {
                    // If the bullet is expected to hit next frame, roll to the safe position.
                    if (WillBulletHitPlayerNextFrame(__instance, dangerousBullets))
                    {
                        SafeDodgeRoll(__instance, dangerousBullets);
                    }
                    // If not, avoid from the bullet.
                    else
                    {
                        HandleBulletAvoidance(__instance, dangerousBullets);
                    }
                }
                // During combat, and keep the distance from the enemies.
                else if (__instance.CurrentRoom.HasActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear))
                {
                    HandleEnemySpacing(__instance);
                }
                // Out of combat, so just follow the cursor.
                else
                {
                    HandleMouseFollow(__instance);
                }
            }

            // Manual dodge roll (right-click)
            if (Input.GetMouseButtonDown(1) && !__instance.IsDodgeRolling)
            {
                Vector2 dodgeDir = (Camera.main.ScreenToWorldPoint(Input.mousePosition).XY() - playerPos).normalized;
                if (dodgeDir.magnitude < 0.1f) dodgeDir = Vector2.right;
                __instance.StartDodgeRoll(dodgeDir);
            }
        }   // end of Postfix method


        // Find the dangerous bullets in the scene.
        public static List<Projectile> DetectDangerousBullets(PlayerController player)
        {
            List<Projectile> dangerousBullets = new List<Projectile>();
            
            Vector2 playerPos = player.CenterPosition;
            float dangerRadius = 2.5f;

            // Always check for the incoming bullets.
            foreach (Projectile proj in StaticReferenceManager.AllProjectiles)
            {
                if (proj == null || !proj.sprite || !proj.isActiveAndEnabled || proj.Owner is PlayerController)
                    continue;

                Vector2 bulletPos = proj.sprite.WorldCenter;
                Vector2 toPlayer = playerPos - bulletPos;
                float distance = toPlayer.magnitude;
                float angleDiff = Vector2.Angle(toPlayer, proj.Direction);

                // A bullet is considered dangerous if it is close to and moving toward the player
                if (distance < dangerRadius && angleDiff < 60f)
                {
                    dangerousBullets.Add(proj);
                }
            }

            // Return the list of dangerous bullets or null.
            if (dangerousBullets.Count <= 0)
                return null;
            return dangerousBullets;
        }


        // Avoid the dangerous bullets found from DetectDangerousBullet.
        public static void HandleBulletAvoidance(PlayerController player, List<Projectile> dangerousBullets)
        {
            Vector2 playerPos = player.CenterPosition;
            float speed = player.stats.MovementSpeed;

            // Calculate the direction that is the average direction from the dangerous bullets
            Vector2 combinedBulletDir = Vector2.zero;
            foreach (Projectile proj in dangerousBullets)
            {
                combinedBulletDir += proj.Direction;
            }
            combinedBulletDir = combinedBulletDir.normalized;

            // Perpendicular directions
            Vector2 perp1 = new Vector2(-combinedBulletDir.y, combinedBulletDir.x);
            Vector2 perp2 = new Vector2(combinedBulletDir.y, -combinedBulletDir.x);

            // Blend perpendicular and backward directions to creat two diagonal directions.
            float retreatWeight = 0.7f; // how much backward to blend
            Vector2 diag1 = (perp1 + combinedBulletDir * retreatWeight).normalized;
            Vector2 diag2 = (perp2 + combinedBulletDir * retreatWeight).normalized;

            // Choose the one that moves the player further from the bullet
            float a1 = Vector2.Angle(player.specRigidbody.Velocity.normalized, diag1);
            float a2 = Vector2.Angle(player.specRigidbody.Velocity.normalized, diag2);

            Vector2 finalDirection = a1 < a2 ? diag1 : diag2;

            // If the player is out of combat and IncreaseSpeedOutOfCombat option is turned on, boost the speed.
            if (!player.CurrentRoom.HasActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear) &&
                GameManager.Options.IncreaseSpeedOutOfCombat)
                speed *= 1.5f;

            TryMove(player, finalDirection, speed);
        }

        // Keep the best distance from the nearest enemy in the scene.
        public static void HandleEnemySpacing(PlayerController player)
        {
            Vector2 playerPos = player.CenterPosition;
            float speed = player.stats.MovementSpeed;
            float idealDistance = 7.0f;
            float distanceThreshold = 0.5f;

            // If there is a boss in current room, increase the distance.
            foreach (AIActor enemy in player?.CurrentRoom.activeEnemies)
            {
                if (enemy == null || !enemy.healthHaver || enemy.healthHaver.IsDead || enemy.CompanionOwner)
                    continue;

                if (enemy.healthHaver.IsBoss)
                {
                    idealDistance = 10.0f;
                    distanceThreshold = 1.0f;
                    break;
                }
            }

            // Get the nearest enemy in the current room.
            AIActor nearestEnemy = player.CurrentRoom.GetNearestEnemy(playerPos, out float distToEnemy, true, true);

            if (nearestEnemy != null)
            {
                Vector2 enemyPos = nearestEnemy.CenterPosition;
                float distance = Vector2.Distance(playerPos, enemyPos);

                // Move away if the enemy is closer than the ideal distance.
                if (distance < idealDistance - distanceThreshold)
                {
                    Vector2 directionAway = (playerPos - enemyPos).normalized;

                    TryMove(player, directionAway, speed);
                }
                // Move toward if the enemy is further than the ideal distance.
                else if (distance > idealDistance + distanceThreshold)
                {
                    Vector2 directionToward = (enemyPos - playerPos).normalized;

                    TryMove(player, directionToward, speed);
                }
                // No need to move once the enemy is within the ideal distance.
                else
                {
                    player.specRigidbody.Velocity = Vector2.zero;
                }
            }
        }

        // Make the player character follow the mouse cursor.
        public static void HandleMouseFollow(PlayerController player)
        {
            Vector2 playerPos = player.CenterPosition;
            float speed = player.stats.MovementSpeed;

            Vector3 mouseWorldPos = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            mouseWorldPos.z = 0f;

            Vector2 mousePos = mouseWorldPos.XY();
            float distance = Vector2.Distance(playerPos, mousePos);

            // If the cursor is some distance away from the character, follow it.
            if (distance > 1.5f)
            {
                Vector2 direction = (mousePos - playerPos).normalized;

                if (GameManager.Options.IncreaseSpeedOutOfCombat)
                    speed *= 1.5f;

                TryMove(player, direction, speed);
            }
            // If not, stop.
            else
            {
                player.specRigidbody.Velocity = Vector2.zero;
            }
        }


        // Validate if the moving direction is not blocked by any obstacles.
        public static bool IsDirectionClear(PlayerController player, Vector2 direction, float distance = 0.5f)
        {
            Vector2 offsetOrigin = player.CenterPosition + new Vector2(0f, -0.5f);
            Vector2 step = direction.normalized * 0.5f; // checks every ~0.5 units
            int steps = Mathf.Max(1, Mathf.CeilToInt(distance / 0.5f));

            for (int i = 1; i <= steps; i++)
            {
                Vector2 pos = offsetOrigin + step * i;
                IntVector2 cell = pos.ToIntVector2(VectorConversions.Floor);

                if (!GameManager.Instance.Dungeon.data.CheckInBoundsAndValid(cell))
                    return false;

                CellData cellData = GameManager.Instance.Dungeon.data[cell];
                if (cellData == null)
                    return false;

                // If the direction contains a wall, pit, or door, return false.
                bool isFloor = cellData.type == CellType.FLOOR;
                bool isDoor = cellData.isDoorFrameCell;
                if (!isFloor || (isDoor && player.IsInCombat))
                    return false;
            }

            return true;
        }

        // Try moving the player.
        public static void TryMove(PlayerController player, Vector2 direction, float speed)
        {
            // If the direction is valid, move.
            if (IsDirectionClear(player, direction))
            {
                player.specRigidbody.Velocity = direction * speed;
            }
            // If the direction is containing any obstacles, try to slide through them.
            else if (player.CurrentRoom.HasActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear))
            {
                Vector2? slide = FindSlideAlongWallDirection(player, direction);
                if (slide.HasValue)
                {
                    player.specRigidbody.Velocity = slide.Value * speed;
                    _lastSlideDirection = slide.Value;
                    _slideCooldownTimer = 0.1f;
                }
                else
                {
                    player.specRigidbody.Velocity = Vector2.zero;
                }
            }
            else
            {
                player.specRigidbody.Velocity = Vector2.zero;
            }
        }

        // Want to move but faced an obstacle? Try to slide along it.
        public static Vector2? FindSlideAlongWallDirection(PlayerController player, Vector2 desiredDirection, float checkDistance = 0.5f)
        {
            Vector2 perpLeft = new Vector2(-desiredDirection.y, desiredDirection.x);   // 90° left
            Vector2 perpRight = new Vector2(desiredDirection.y, -desiredDirection.x);  // 90° right

            if (_lastSlideDirection.HasValue && IsDirectionClear(player, _lastSlideDirection.Value, checkDistance))
            {
                return _lastSlideDirection;
            }

            if (IsDirectionClear(player, perpLeft, checkDistance))
            {
                _lastSlideDirection = perpLeft;
                _slideCooldownTimer = 0.1f;
                return perpLeft;
            }

            if (IsDirectionClear(player, perpRight, checkDistance))
            {
                _lastSlideDirection = perpRight;
                _slideCooldownTimer = 0.1f;
                return perpRight;
            }

            return null;
        }


        // Literally, check AABB collision of given two objects.
        public static bool CheckAABBCollision(PixelCollider a, Vector2 aPos, PixelCollider b, Vector2 bPos, float padding = 10.0f)
        {
            float aMinX = aPos.x + a.MinX - padding;
            float aMaxX = aPos.x + a.MaxX + padding;
            float aMinY = aPos.y + a.MinY - padding;
            float aMaxY = aPos.y + a.MaxY + padding;

            float bMinX = bPos.x + b.MinX - padding;
            float bMaxX = bPos.x + b.MaxX + padding;
            float bMinY = bPos.y + b.MinY - padding;
            float bMaxY = bPos.y + b.MaxY + padding;

            return (aMinX <= bMaxX && aMaxX >= bMinX && aMinY <= bMaxY && aMaxY >= bMinY ||
                    aMaxX <= bMinX && aMinX >= bMaxX && aMaxY <= bMinY && aMinY >= bMaxY);
        }

        // Check if the player will get hit by the dangerous bullet next frame.
        public static bool WillBulletHitPlayerNextFrame(PlayerController player, List<Projectile> dangerousBullets)
        {
            // Calculate the player's position in next frame.
            Vector2 playerPos = player.CenterPosition;
            Vector2 playerVel = player.specRigidbody.Velocity;
            Vector2 nextPlayerPos = playerPos + playerVel * BraveTime.DeltaTime;

            PixelCollider playerCollider = player.specRigidbody?.HitboxPixelCollider;
            if (playerCollider == null)
                return false;

            foreach (Projectile proj in dangerousBullets)
            {
                // Calculate the dangerous bullet's position in next frame.
                Vector2 bulletPos = proj.sprite.WorldCenter;
                Vector2 bulletVel = proj.Direction.normalized * proj.Speed;
                Vector2 nextBulletPos = bulletPos + bulletVel * BraveTime.DeltaTime;

                PixelCollider bulletCollider = proj.specRigidbody?.PrimaryPixelCollider;
                if (bulletCollider == null)
                    return false;

                // Calculate the collision using future positions and their collider information.
                return CheckAABBCollision(bulletCollider, nextBulletPos, playerCollider, nextPlayerPos);
            }

            return false;
        }

        // Is the player about to get hit next frame? Find the best direction to roll.
        public static void SafeDodgeRoll(PlayerController player, List<Projectile> dangerousBullets)
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

                float dist = Vector2.Distance(player.CenterPosition, enemy.CenterPosition);
                if (dist <= threatRange)
                {
                    threatVector += (enemy.CenterPosition - player.CenterPosition).normalized;
                    threatCount++;
                }
            }
            Vector2 combinedThreatDir = threatCount > 0 ? threatVector.normalized : Vector2.zero;


            foreach (Vector2 dir in testDirs)
            {
                // Dodge roll in Enter the Gungeon moves the player about 5.5 units.
                const float ROLL_DISTANCE = 5.5f;
                const float ROLL_TIME = 0.7f;

                // Skip if the direction contains obstacles
                if (!IsDirectionClear(player, dir, ROLL_DISTANCE))
                    continue;

                // The expected destination of the roll.
                Vector2 rollTarget = player.CenterPosition + dir * ROLL_DISTANCE;


                // 1. Away from the enemies score (further is safer)
                float awayFromEnemyScore = 0.0f;
                if (combinedThreatDir != Vector2.zero)
                {
                    awayFromEnemyScore = Vector2.Angle(dir, combinedThreatDir); // max = 180 when rolling away
                }


                // Iterate through all enemy bullets in the room.
                float minBulletDist = float.MaxValue;
                int bulletsNearDestination = 0;
                foreach (Projectile proj in StaticReferenceManager.AllProjectiles)
                {
                    if (proj.Owner is PlayerController)
                        continue;

                    // The expected destination of the bullet.
                    Vector2 futureBulletPos = proj.specRigidbody.UnitCenter + proj.Direction.normalized * proj.Speed * ROLL_TIME;

                    // Calculate the distance between each destination of the roll and bullet.
                    float dist = Vector2.Distance(rollTarget, futureBulletPos);

                    // 2. Nearest bullet distance score (further is safer)
                    if (dist < minBulletDist)
                        minBulletDist = dist;

                    // 3. Bullet count nearby destination score (less is safer)
                    if (dist < 5.0f)
                        bulletsNearDestination++;
                }
                float bulletDistanceScore = minBulletDist;
                float bulletCountDeduction = bulletsNearDestination;


                float perpendicularScoreTotal = 0.0f;
                foreach (Projectile proj in dangerousBullets)
                {
                    float angleDiff = Vector2.Angle(dir, proj.Direction.normalized);
                    float perpendicularScore = 90f - Mathf.Abs(90f - angleDiff); // Max at 90°, 0 at 0°/180°

                    // 4. Bullet alignment score (prefer perpendicular)
                    perpendicularScoreTotal += perpendicularScore;
                }
                float avgPerpendicularScore = perpendicularScoreTotal / Mathf.Max(1, dangerousBullets.Count);



                // Combine all scores
                float score = awayFromEnemyScore * 0.3f + bulletDistanceScore * 1.5f + avgPerpendicularScore * 0.1f - Mathf.Min(bulletCountDeduction * 2.0f, 5.0f);
                ETGModConsole.Log("[DEBUG] Direction = (" + dir.x + ", " + dir.y + ") Score = " + awayFromEnemyScore * 0.3f + " + " + bulletDistanceScore * 1.5f + " + " + avgPerpendicularScore * 0.1f + " - " + Mathf.Min(bulletCountDeduction * 2.0f, 5.0f) + " = " + score);

                // Update the best score
                if (score > bestScore)
                {
                    bestScore = score;
                    bestDirection = dir;
                }
            }

            // Dodge roll toward the best direction
            if (!player.IsDodgeRolling)
            {
                player.StartDodgeRoll(bestDirection);
            }
            ETGModConsole.Log("[DEBUG] End of SafeDodgeRoll");
        }
    }
}
