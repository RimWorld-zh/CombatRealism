﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using RimWorld;
using Verse;
using UnityEngine;

namespace Combat_Realism
{
    public class Verb_LaunchProjectileCR : Verb
    {
        public VerbPropertiesCR verbPropsCR
        {
            get
            {
                return verbProps as VerbPropertiesCR;
            }
        }
        public ProjectilePropertiesCR projectilePropsCR
        {
            get
            {
                return projectileDef.projectile as ProjectilePropertiesCR;
            }
        }

        // Returns either the pawn aiming the weapon or in case of turret guns the turret operator or null if neither exists
        public Pawn ShooterPawn
        {
            get
            {
                if (CasterPawn != null)
                {
                    return CasterPawn;
                }
                return CR_Utility.TryGetTurretOperator(caster);
            }
        }

        // Cover check constants
        private const float distToCheckForCover = 3f;   // How many cells to raycast on the cover check
        private const float segmentLength = 0.2f;       // How long a single raycast segment is
        private const float shotHeightFactor = 0.85f;   // The height at which pawns hold their guns

        // Targeting factors
        private float estimatedTargDist = -1;           // Stores estimate target distance for each burst, so each burst shot uses the same
        private int numShotsFired = 0;                  // Stores how many shots were fired for purposes of recoil
        private float shotAngle;
        private float shotHeight;

        protected CompCharges compChargesInt = null;
        protected CompCharges compCharges
        {
            get
            {
                if (compChargesInt == null && ownerEquipment != null)
                {
                    compChargesInt = ownerEquipment.TryGetComp<CompCharges>();
                }
                return compChargesInt;
            }
        }
        private float shotSpeedInt = -1;
        private float shotSpeed
        {
            get
            {
                if (shotSpeedInt < 0)
                {
                    shotSpeedInt = verbProps.projectileDef.projectile.speed;
                    if (compCharges != null)
                    {
                        Vector2 bracket;
                        if (compCharges.GetChargeBracket((currentTarget.Cell - caster.Position).LengthHorizontal, out bracket))
                        {
                            shotSpeedInt = bracket.x;
                        }
                    }
                    else
                    {
                        shotSpeedInt = verbProps.projectileDef.projectile.speed;
                    }
                }
                return shotSpeedInt;
            }
        }

        protected float shootingAccuracy
        {
            get
            {
                if (CasterPawn != null)
                {
                    return CasterPawn.GetStatValue(StatDefOf.ShootingAccuracy, false);
                }
                return 2f;
            }
        }
        protected float aimingAccuracy
        {
            get
            {
                // Aim is influenced by turret operator if one exists
                if (ShooterPawn != null)
                {
                    return ShooterPawn.GetStatValue(CR_StatDefOf.AimingAccuracy);
                }
                return 0.75f;
            }
        }
        protected float aimEfficiency
        {
            get
            {
                return (3 - ownerEquipment.GetStatValue(CR_StatDefOf.AimEfficiency));
            }
        }
        protected virtual float swayAmplitude
        {
            get
            {
                return (4.5f - shootingAccuracy) * ownerEquipment.GetStatValue(CR_StatDefOf.SwayFactor);
            }
        }

        // Ammo variables
        protected CompAmmoUser compAmmoInt = null;
        protected CompAmmoUser compAmmo
        {
            get
            {
                if (compAmmoInt == null && ownerEquipment != null)
                {
                    compAmmoInt = ownerEquipment.TryGetComp<CompAmmoUser>();
                }
                return compAmmoInt;
            }
        }
        private ThingDef projectileDef
        {
            get
            {
                if (compAmmo != null)
                {
                    if (compAmmo.currentAmmo != null)
                    {
                        return compAmmo.currentAmmo.linkedProjectile;
                    }
                }
                return verbPropsCR.projectileDef;
            }
        }

        /// <summary>
        /// Highlights explosion radius of the projectile if it has one
        /// </summary>
        /// <returns>Projectile explosion radius</returns>
        public override float HighlightFieldRadiusAroundTarget()
        {
            return projectileDef.projectile.explosionRadius;
        }

        /// <summary>
        /// Calculates the shot angle necessary to hit the designated target
        /// </summary>
        /// <param name="velocity">projectile velocity in cells per second</param>
        /// <param name="range">cells between shooter and target</param>
        /// <param name="heightDifference">difference between initial shot height and target height</param>
        /// <returns>lower arc angle in radians</returns>
        private float GetShotAngle(float velocity, float range, float heightDifference)
        {
            const float gravity = CR_Utility.gravityConst;
            float angle = 0;
            angle = (float)Math.Atan((Math.Pow(velocity, 2) + (projectileDef.projectile.flyOverhead ? 1 : -1) * Math.Sqrt(Math.Pow(velocity, 4) - gravity * (gravity * Math.Pow(range, 2) + 2 * heightDifference * Math.Pow(velocity, 2)))) / (gravity * range));
            return angle;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="velocity">projectile velocity in cells per second</param>
        /// <param name="angle">shot angle in radians</param>
        /// <param name="shotHeight">height from which projectile is fired</param>
        /// <returns>distance in cells projectile will fly at given arc</returns>
        private float GetDistanceTraveled(float velocity, float angle, float shotHeight)
        {
            const float gravity = CR_Utility.gravityConst;
            float distance = (float)((velocity * Math.Cos(angle)) / gravity) * (float)(velocity * Math.Sin(angle) + Math.Sqrt(Math.Pow(velocity * Math.Sin(angle), 2) + 2 * gravity * shotHeight));
            return distance;
        }

        /// <summary>
        /// Resets current burst shot count and estimated distance at beginning of the burst
        /// </summary>
        public override void WarmupComplete()
        {
            numShotsFired = 0;
            estimatedTargDist = -1;
            base.WarmupComplete();
        }

        /// <summary>
        /// Shifts the original target position in accordance with target leading, range estimation and weather/lighting effects
        /// </summary>
        protected virtual Vector3 ShiftTarget(ShiftVecReport report)
        {
            // ----------------------------------- STEP 0: Actual location

            Vector3 targetLoc = report.targetPawn != null ? Vector3.Scale(report.targetPawn.DrawPos, new Vector3(1, 0, 1)) : report.target.Cell.ToVector3Shifted();
            Vector3 sourceLoc = CasterPawn != null ? Vector3.Scale(CasterPawn.DrawPos, new Vector3(1, 0, 1)) : caster.Position.ToVector3Shifted();

            // ----------------------------------- STEP 1: Shift for visibility

            Vector2 circularShiftVec = report.GetRandCircularVec();
            Vector3 newTargetLoc = targetLoc;
            newTargetLoc.x += circularShiftVec.x;
            newTargetLoc.z += circularShiftVec.y;

            // ----------------------------------- STEP 2: Estimated shot to hit location

            // On first shot of burst do a range estimate
            if (estimatedTargDist < 0)
            {
                estimatedTargDist = report.GetRandDist();
            }
            newTargetLoc = sourceLoc + (newTargetLoc - sourceLoc).normalized * estimatedTargDist;

            // Lead a moving target
            newTargetLoc += report.GetRandLeadVec();

            // ----------------------------------- STEP 3: Recoil, Skewing, Skill checks, Cover calculations

            Vector2 skewVec = new Vector2(0, 0);
            skewVec += GetSwayVec();
            skewVec += GetRecoilVec();

            // Height difference calculations for ShotAngle
            float heightDifference = 0;
            float targetableHeight = 0;

            // Projectiles with flyOverhead target the ground below the target and ignore cover
            if (!projectileDef.projectile.flyOverhead)
            {
                targetableHeight = CR_Utility.GetCollisionHeight(currentTarget.Thing);
                if (report.cover != null)
                {
                    targetableHeight += CR_Utility.GetCollisionHeight(report.cover);
                }
                heightDifference += targetableHeight * 0.5f;    //Optimal hit level is halfway
            }

            shotHeight = CR_Utility.GetCollisionHeight(caster);
            if (CasterPawn != null)
            {
                shotHeight *= shotHeightFactor;
            }
            heightDifference -= shotHeight;
            skewVec += new Vector2(0, GetShotAngle(shotSpeed, (newTargetLoc - sourceLoc).magnitude, heightDifference) * (180 / (float)Math.PI));

            // ----------------------------------- STEP 4: Mechanical variation

            // Get shotvariation
            Vector2 spreadVec = report.GetRandSpreadVec();
            skewVec += spreadVec;

            // Skewing		-		Applied after the leading calculations to not screw them up
            float distanceTraveled = GetDistanceTraveled(shotSpeed, (float)(skewVec.y * (Math.PI / 180)), shotHeight);
            newTargetLoc = sourceLoc + ((newTargetLoc - sourceLoc).normalized * distanceTraveled);
            newTargetLoc = sourceLoc + (Quaternion.AngleAxis(skewVec.x, Vector3.up) * (newTargetLoc - sourceLoc));

            shotAngle = (float)(skewVec.y * (Math.PI / 180));

            return newTargetLoc;
        }

        /// <summary>
        /// Calculates the amount of recoil at a given point in a burst, up to a maximum
        /// </summary>
        /// <returns>Vector by which to shift the target</returns>
        private Vector2 GetRecoilVec()
        {
            float minX = 0;
            float maxX = 0;
            float minY = 0;
            float maxY = 0;
            switch (verbPropsCR.recoilPattern)
            {
                case RecoilPattern.None:
                    return new Vector2(0, 0);
                case RecoilPattern.Regular:
                    float num = verbPropsCR.recoilAmount / 3;
                    minX = -(num / 3);
                    maxX = num;
                    minY = -num;
                    maxY = verbPropsCR.recoilAmount;
                    break;
                case RecoilPattern.Mounted:
                    float num2 = verbPropsCR.recoilAmount / 3;
                    minX = -num2;
                    maxX = num2;
                    minY = -num2;
                    maxX = verbPropsCR.recoilAmount;
                    break;
            }
            float recoilX = UnityEngine.Random.Range(minX, maxX);
            float recoilY = UnityEngine.Random.Range(minY, maxY);
            
            float recoilMagnitude = Mathf.Pow((5 - shootingAccuracy), (Mathf.Min(10, numShotsFired) / 6.25f));
            return new Vector2(recoilX, recoilY) * recoilMagnitude;
        }

        /// <summary>
        /// Calculates current weapon sway based on a parametric function with maximum amplitude depending on shootingAccuracy and scaled by weapon's swayFactor.
        /// </summary>
        /// <returns>Vector2 with weapon skew in degrees</returns>
        protected Vector2 GetSwayVec()
        {
            int ticks = Find.TickManager.TicksAbs + caster.thingIDNumber;
            Vector2 swayVec = new Vector2(swayAmplitude * (float)Math.Sin(ticks * (0.022f)), swayAmplitude * (float)Math.Sin(ticks * 0.0165f));
            swayVec.y *= 0.25f;
            return swayVec;
        }

        public virtual ShiftVecReport ShiftVecReportFor(TargetInfo target)
        {
            IntVec3 targetCell = target.Cell;
            ShiftVecReport report = new ShiftVecReport();
            report.target = target;
            report.aimingAccuracy = aimingAccuracy;
            report.aimEfficiency = aimEfficiency;
            report.shotDist = (targetCell - caster.Position).LengthHorizontal;

            report.lightingShift = 1 - Find.GlowGrid.GameGlowAt(targetCell);
            if (!caster.Position.Roofed() || !targetCell.Roofed())  //Change to more accurate algorithm?
            {
                report.weatherShift = 1 - Find.WeatherManager.CurWeatherAccuracyMultiplier;
            }
            report.shotSpeed = shotSpeed;
            report.swayDegrees = swayAmplitude;
            report.spreadDegrees = ownerEquipment.GetStatValue(CR_StatDefOf.ShotSpread) * projectilePropsCR.spreadMult;
            Thing cover;
            GetPartialCoverBetween(caster.Position.ToVector3Shifted(), targetCell.ToVector3Shifted(), out cover);
            report.cover = cover;

            return report;
        }

        /// <summary>
        /// Checks for cover along the flight path of the bullet, doesn't check for walls or plants, only intended for cover with partial fillPercent
        /// </summary>
        /// <param name="sourceLoc">The position from which to start checking</param>
        /// <param name="targetLoc">The position of the target</param>
        /// <param name="cover">Output parameter, filled with the highest cover object found</param>
        /// <returns>True if cover was found, false otherwise</returns>
        private bool GetPartialCoverBetween(Vector3 sourceLoc, Vector3 targetLoc, out Thing cover)
        {
            sourceLoc.Scale(new Vector3(1, 0, 1));
            targetLoc.Scale(new Vector3(1, 0, 1));

            //Calculate segment vector and segment amount
            Vector3 shotVec = sourceLoc - targetLoc;    //Vector from target to source
            Vector3 segmentVec = shotVec.normalized * segmentLength;
            float distToCheck = Mathf.Min(distToCheckForCover, shotVec.magnitude);  //The distance to raycast
            float numSegments = distToCheck / segmentLength;

            //Raycast accross all segments to check for cover
            List<IntVec3> checkedCells = new List<IntVec3>();
            Thing thingAtTargetLoc = GridsUtility.GetEdifice(targetLoc.ToIntVec3());
            Thing newCover = null;
            for (int i = 0; i <= numSegments; i++)
            {
                IntVec3 cell = (targetLoc + segmentVec * i).ToIntVec3();
                if (!checkedCells.Contains(cell))
                {
                    //Cover check, if cell has cover compare fillPercent and get the highest piece of cover, ignore if cover is the target (e.g. solar panels, crashed ship, etc)
                    Thing coverAtCell = GridsUtility.GetCover(cell);
                    if (coverAtCell != null
                        && (thingAtTargetLoc == null || !coverAtCell.Equals(thingAtTargetLoc))
                        && (newCover == null || newCover.def.fillPercent < coverAtCell.def.fillPercent)
                        && coverAtCell.def.Fillage != FillCategory.Full
                        && coverAtCell.def.category != ThingCategory.Plant)
                    {
                        newCover = coverAtCell;
                    }
                }
            }
            cover = newCover;

            //Report success if found cover
            return cover != null;
        }

        /// <summary>
        /// Checks if the shooter can hit the target from a certain position with regards to cover height
        /// </summary>
        /// <param name="root">The position from which to check</param>
        /// <param name="targ">The target to check for line of sight</param>
        /// <returns>True if shooter can hit target from root position, false otherwise</returns>
        public override bool CanHitTargetFrom(IntVec3 root, TargetInfo targ)
        {
            //Sanity check for flyOverhead projectiles, they should not attack things under thick roofs
            if (projectileDef.projectile.flyOverhead)
            {
                RoofDef roofDef = Find.RoofGrid.RoofAt(targ.Cell);
                if (roofDef != null && roofDef.isThickRoof)
                {
                    return false;
                }
                return base.CanHitTargetFrom(root, targ);
            }

            if (base.CanHitTargetFrom(root, targ))
            {
                //Check if target is obstructed behind cover
                Thing coverTarg;
                if (GetPartialCoverBetween(root.ToVector3Shifted(), targ.Cell.ToVector3Shifted(), out coverTarg))
                {
                    float targetHeight = CR_Utility.GetCollisionHeight(targ.Thing);
                    if (targetHeight <= CR_Utility.GetCollisionHeight(coverTarg))
                    {
                        return false;
                    }
                }
                //Check if shooter is obstructed by cover
                Thing coverShoot;
                if (GetPartialCoverBetween(targ.Cell.ToVector3Shifted(), root.ToVector3Shifted(), out coverShoot))
                {
                    float shotHeight = CR_Utility.GetCollisionHeight(caster);
                    if (CasterPawn != null)
                    {
                        shotHeight *= shotHeightFactor;
                    }
                    if (shotHeight <= CR_Utility.GetCollisionHeight(coverShoot))
                    {
                        return false;
                    }
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// Fires a projectile using the new aiming system
        /// </summary>
        /// <returns>True for successful shot, false otherwise</returns>
        protected override bool TryCastShot()
        {
            ShootLine shootLine;
            if (!TryFindShootLineFromTo(caster.Position, currentTarget, out shootLine))
            {
                return false;
            }
            if (projectilePropsCR.pelletCount < 1)
            {
                Log.Error(ownerEquipment.LabelCap + " tried firing with pelletCount less than 1.");
                return false;
            }
            for (int i = 0; i < projectilePropsCR.pelletCount; i++)
            {
                Vector3 casterExactPosition = caster.DrawPos;
                ProjectileCR projectile = (ProjectileCR)ThingMaker.MakeThing(projectileDef, null);
                GenSpawn.Spawn(projectile, shootLine.Source);
                float lengthHorizontalSquared = (currentTarget.Cell - caster.Position).LengthHorizontalSquared;

                //New aiming algorithm
                projectile.canFreeIntercept = true;
                ShiftVecReport report = ShiftVecReportFor(currentTarget);
                Vector3 targetVec3 = ShiftTarget(report);
                projectile.shotAngle = shotAngle;
                projectile.shotHeight = shotHeight;
                projectile.shotSpeed = shotSpeed;
                if (currentTarget.Thing != null)
                {
                    projectile.Launch(caster, casterExactPosition, new TargetInfo(currentTarget.Thing), targetVec3, ownerEquipment);
                }
                else
                {
                    projectile.Launch(caster, casterExactPosition, new TargetInfo(shootLine.Dest), targetVec3, ownerEquipment);
                }
            }
            numShotsFired++;
            return true;
        }

        /// <summary>
        /// This is a custom CR ticker. Since the vanilla VerbTick() method is non-virtual we need to detour VerbTracker and make it call this method in addition to the vanilla ticker in order to
        /// add custom ticker functionality.
        /// </summary>
        public virtual void VerbTickCR()
        {
        }
    }
}
