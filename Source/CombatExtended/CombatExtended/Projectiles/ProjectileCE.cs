﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using CombatExtended.Compatibility;
using CombatExtended.Lasers;
using ProjectileImpactFX;
using RimWorld.Planet;
using CombatExtended.Utilities;

namespace CombatExtended
{
    [StaticConstructorOnStartup]
    public abstract class ProjectileCE : ThingWithComps
    {
        #region ClassVariables
        /// <summary>
        /// Suppression is applied within this radius (x-y and z)
        /// </summary>
        protected const int SuppressionRadius = 3;

        /// <summary>
        /// Check for collision with multi-cell pawns and apply suppression in radius of this size, centered on flight path.
        /// </summary>
        protected const int collisionCheckSize = 5;

        #region Kinetic Projectiles
        protected bool lerpPosition = true;
        protected bool kinit = false;
        protected float ballisticCoefficient;
        protected float mass;
        protected float radius;
        protected float gravity;
        protected Vector3 velocity;
        protected float initialSpeed;
        #endregion

        #region Origin destination
        public bool OffMapOrigin = false;

        public Vector2 origin;

        private IntVec3 originInt = new IntVec3(0, -1000, 0);
        public IntVec3 OriginIV3
        {
            get
            {
                if (originInt.y < 0)
                {
                    originInt = new IntVec3(origin);
                }
                return originInt;
            }
        }

        public Vector3 destinationInt = new Vector3(0f, 0f, -1f);
        /// <summary>
        /// Calculates the destination (zero height) reached with a projectile of speed <i>shotSpeed</i> fired at <i>shotAngle</i> from height <i>shotHeight</i> starting from <i>origin</i>. Does not take into account air resistance.
        /// </summary>
        public Vector2 Destination
        {
            get
            {
                if (destinationInt.z < 0)
                {
                    destinationInt = origin + Vector2.up.RotatedBy(shotRotation) * DistanceTraveled;
                    destinationInt.z = 0f;
                }
                // Since returning as a Vector2 yields Vector2(Vector3.x, Vector3.y)!
                return destinationInt;
            }
        }
        #endregion

        /// <summary>
        /// Determine whether the pawn that fired this projectile (if it was a pawn)
        /// should be considered guilty if this projectile hits a friendly target.
        /// </summary>
        /// <remarks>
        /// This effectively aims to prevent people drafting pawns and ordering them to attack friendly targets to cheese guilt.
        /// </remarks>
        protected bool InstigatorGuilty => !(launcher is Pawn launcherPawn && launcherPawn.Drafted);

        public Thing intendedTargetThing
        {
            get
            {
                return intendedTarget.Thing;
            }
        }

        /// <summary>
        /// Backing field for <see cref="DamageAmount"/>.
        /// </summary>
        protected float? damageAmount;

        /// <summary>
        /// Return the damage dealt by this projectile scaled by the quality multiplier of its launcher.
        /// </summary>
        public virtual float DamageAmount
        {
            get
            {
                if (this.damageAmount == null)
                {
                    // Apply a multiplier to bullet damage based on the quality of the weapon that fired it
                    var weaponDamageMultiplier = equipment?.GetStatValue(StatDefOf.RangedWeapon_DamageMultiplier) ?? 1f;
                    this.damageAmount = def.projectile.GetDamageAmount(weaponDamageMultiplier);
                }

                if (lerpPosition)
                {
                    return (float)this.damageAmount;
                }
                return ((float)this.damageAmount) * (shotSpeed * shotSpeed) / (initialSpeed * initialSpeed);
            }
        }

        /// <summary>
        /// Reference to the weapon that fired this projectile, may be null.
        /// </summary>
        public Thing equipment;

        public ThingDef equipmentDef;
        public Thing launcher;
        public LocalTargetInfo intendedTarget;
        public float minCollisionDistance;
        public bool canTargetSelf;
        public bool castShadow = true;
        public bool logMisses = true;

        public GlobalTargetInfo globalTargetInfo = GlobalTargetInfo.Invalid;
        public GlobalTargetInfo globalSourceInfo = GlobalTargetInfo.Invalid;

        #region Vanilla
        public bool landed;
        public int ticksToImpact;
        protected Sustainer ambientSustainer;

        #endregion

        protected float suppressionAmount;
        public Thing mount; // GiddyUp compatibility, ignore collisions with pawns the launcher is mounting
        public float AccuracyFactor;

        #region Height
        public virtual float Height
        {
            get
            {
                return ExactPosition.y;
            }
        }
        #endregion

        #region Ticks/Seconds
        protected float startingTicksToImpactInt = -1f;
        public float StartingTicksToImpact
        {
            get
            {
                if (!lerpPosition)
                {
                    return float.MaxValue;
                }
                if (startingTicksToImpactInt < 0f)
                {
                    // Optimization in case shotHeight is zero (for example for fragments)
                    if (shotHeight < 0.001f)
                    {
                        // Opt-out in case the projectile is to collide instantly
                        if (shotAngle < 0f)
                        {
                            destinationInt = origin;
                            startingTicksToImpactInt = 0f;
                            // During drawing in Multiplayer - impact causes issues. Will get handled inside of the `Tick` call.
                            // In the future, replace this with `!InInterface` call, as it's more fitting here.
                            if (!global::CombatExtended.Compatibility.Multiplayer.InMultiplayer)
                            {
                                ImpactSomething();
                            }
                            return 0f;
                        }
                        // Multiplied by ticksPerSecond since the calculated time is actually in seconds.
                        startingTicksToImpactInt = (float)((origin - Destination).magnitude / (Mathf.Cos(shotAngle) * shotSpeed)) * (float)GenTicks.TicksPerRealSecond;
                        return startingTicksToImpactInt;
                    }
                    startingTicksToImpactInt = GetFlightTime() * (float)GenTicks.TicksPerRealSecond;
                }
                return startingTicksToImpactInt;
            }
        }

        int intTicksToImpact = -1;
        /// <summary>
        /// An integer ceil value of StartingTicksToImpact. intTicksToImpact is equal to -1 when not initialized.
        /// </summary>
        public int IntTicksToImpact
        {
            get
            {
                if (!lerpPosition)
                {
                    return 1;
                }
                if (intTicksToImpact < 0)
                {
                    intTicksToImpact = Mathf.CeilToInt(StartingTicksToImpact);
                }
                return intTicksToImpact;
            }
        }

        private int flightTicks;
        /// <summary>
        /// The amount of integer ticks this projectile has remained in the air for, ignoring impact.
        /// </summary>
        public int FlightTicks
        {
            get
            {
                return flightTicks;
            }
        }
        /// <summary>
        /// The amount of float ticks the projectile has remained in the air for, including impact.
        /// </summary>
        public float fTicks
        {
            get
            {
                return (ticksToImpact == 0 ? StartingTicksToImpact : (float)FlightTicks);
            }
        }
        #endregion

        #region Position
        protected virtual Vector2 Vec2Position(float ticks = -1f)
        {
            if (ticks < 0)
            {
                ticks = fTicks;
            }
            return Vector2.Lerp(origin, Destination, ticks / StartingTicksToImpact);
        }

        private Vector3? exactPosition = null;
        /// <summary>
        /// Exact x,y,z (x,height,y) position in terms of Vec2Position.x, .y (lerped origin to Destination) and Height.
        /// </summary>
        public virtual Vector3 ExactPosition
        {
            set
            {
                exactPosition = new Vector3(value.x, value.y, value.z);
                Position = ((Vector3)exactPosition).ToIntVec3();
            }
            get
            {
                if (exactPosition == null)
                {
                    exactPosition = new Vector3(origin.x, shotHeight, origin.y);
                }
                return ((Vector3)exactPosition);
            }
        }

        public virtual Vector2 DrawPosV2
        {
            get
            {
                return new Vector2(ExactPosition.x, ExactPosition.z);
            }
        }

        public override Vector3 DrawPos
        {
            get
            {
                return ExactPosition;
            }
        }

        private Vector3 lastExactPos = new Vector3(-1000, 0, 0);
        public Vector3 LastPos
        {
            protected set
            {
                lastExactPos = value;
            }
            get
            {
                if (lastExactPos.x < -999)
                {
                    var lastPos = Vec2Position(FlightTicks - 1);
                    lastExactPos = new Vector3(lastPos.x, GetHeightAtTicks(FlightTicks - 1), lastPos.y);
                }
                return lastExactPos;
            }
        }
        public Vector3 PreLastPos
        {
            get
            {
                if (FlightTicks > 2)
                {
                    var preLastPos = Vec2Position(FlightTicks - 2);
                    return new Vector3(preLastPos.x, GetHeightAtTicks(FlightTicks - 2), preLastPos.y);
                }
                else
                {
                    return LastPos;
                }
            }
        }

        public Vector3 ExactMinusLastPos
        {
            get
            {
                return (ExactPosition - LastPos);
            }
        }

        protected DangerTracker _dangerTracker = null;
        protected DangerTracker DangerTracker
        {
            get
            {
                return _dangerTracker ?? (_dangerTracker = Map.GetDangerTracker());
            }
        }

        private int lastShotLine = -1;
        private Ray shotLine;
        public Ray ShotLine
        {
            get
            {
                if (lastShotLine != FlightTicks)
                {
                    shotLine = new Ray(LastPos, ExactMinusLastPos);
                    lastShotLine = FlightTicks;
                }
                return shotLine;
            }
        }
        #endregion

        #region Angle
        /// <summary>
        /// Based on equations of motion
        /// </summary>
        public Quaternion DrawRotation
        {
            get
            {
                var w = (Destination - origin);

                var vx = w.x / StartingTicksToImpact;

                var vy = (w.y - shotHeight) / StartingTicksToImpact
                         + shotSpeed * Mathf.Sin(shotAngle) / GenTicks.TicksPerRealSecond
                         - (GravityFactor * fTicks) / (GenTicks.TicksPerRealSecond * GenTicks.TicksPerRealSecond);

                return Quaternion.AngleAxis(
                           Mathf.Rad2Deg * Mathf.Atan2(-vy, vx) + 90f
                           , Vector3.up);
            }
        }

        public virtual Quaternion ExactRotation
        {
            get
            {
                return Quaternion.AngleAxis(shotRotation, Vector3.down);
            }
        }
        #endregion

        /// <summary>
        /// Angle off the ground [radians].
        /// </summary>
        public float shotAngle = 0f;
        /// <summary>
        /// Angle rotation between shooter and positive y-vector [degrees]. North: 0f, East: 90f, South: 180f, West: 270f.
        /// </summary>
        public float shotRotation = 0f;
        /// <summary>
        /// Shot height in vertical cells. Humans start their shot at 0.85f [vcells].
        /// </summary>
        public float shotHeight = 0f;
        /// <summary>
        /// The assigned shot speed [cells/s] (not speed in y axis or x-z plane), in general equal to the projectile.def.speed value.
        /// </summary>
        public float shotSpeed = -1f;

        private float _gravityFactor = -1;

        /// <summary>
        /// Gravity factor in meters(cells) per second squared
        /// </summary>
        private float GravityFactor
        {
            get
            {
                if (_gravityFactor < 0)
                {
                    _gravityFactor = CE_Utility.GravityConst;
                    if (def.projectile is ProjectilePropertiesCE props)
                    {
                        _gravityFactor = props.Gravity;
                    }
                }
                return _gravityFactor;
            }
        }



        protected Material[] shadowMaterial;
        protected Material ShadowMaterial
        {
            get
            {
                if (shadowMaterial == null)
                {
                    //Get fully black version of this.Graphic
                    if (Graphic is Graphic_Collection g)
                    {
                        shadowMaterial = GetShadowMaterial(g);
                    }
                    else
                    {
                        shadowMaterial = new Material[1];
                        shadowMaterial[0] = Graphic.GetColoredVersion(ShaderDatabase.Transparent, Color.black, Color.black).MatSingle;
                    }
                }

                return shadowMaterial[Rand.Range(0, this.shadowMaterial.Length)];
            }
        }
        #endregion

        /*
         * *** End of class variables ***
        */

        #region Methods

        #region Expose
        /// <summary>
        /// Saves new variables shotAngle, shotHeight, shotSpeed.
        /// </summary>
        public override void ExposeData()
        {
            base.ExposeData();

            if (Scribe.mode == LoadSaveMode.Saving && launcher != null && launcher.Destroyed)
            {
                launcher = null;
            }
            CE_Scriber.Late(this, (id) =>
            {
                Scribe_References.Look<Thing>(ref launcher, "launcher_" + id);
            });
            Scribe_TargetInfo.Look(ref globalSourceInfo, "globalSourceInfo");
            Scribe_TargetInfo.Look(ref globalTargetInfo, "globalTargetInfo");
            Scribe_TargetInfo.Look(ref intendedTarget, "intendedTarget");

            Scribe_Values.Look<Vector2>(ref origin, "origin", default(Vector2), true);
            Scribe_Values.Look<int>(ref ticksToImpact, "ticksToImpact", 0, true);
            Scribe_References.Look<Thing>(ref launcher, "launcher");
            Scribe_References.Look<Thing>(ref equipment, "equipment");
            Scribe_Values.Look<int>(ref ticksToImpact, "ticksToImpact", 0, true);
            Scribe_Defs.Look<ThingDef>(ref equipmentDef, "equipmentDef");
            Scribe_Values.Look<bool>(ref landed, "landed");
            //Here be new variables
            Scribe_Values.Look(ref shotAngle, "shotAngle", 0f, true);
            Scribe_Values.Look(ref shotRotation, "shotRotation", 0f, true);
            Scribe_Values.Look(ref shotHeight, "shotHeight", 0f, true);
            Scribe_Values.Look(ref shotSpeed, "shotSpeed", 0f, true);
            Scribe_Values.Look<bool>(ref canTargetSelf, "canTargetSelf");
            Scribe_Values.Look<bool>(ref logMisses, "logMisses", true);
            Scribe_Values.Look<bool>(ref castShadow, "castShadow", true);
            Scribe_Values.Look<bool>(ref lerpPosition, "lerpPosition", true);

            //To fix landed grenades sl problem
            Scribe_Values.Look(ref exactPosition, "exactPosition");
            // To insure saves don't get affected..
        }
        #endregion

        #region Throw
        public virtual void Throw(Thing launcher, Vector3 origin, Vector3 heading, Thing equipment = null)
        {
            this.ExactPosition = origin;
            this.shotHeight = origin.y;
            this.origin = new Vector2(origin.x, origin.z);
            this.shotSpeed = Math.Max(heading.magnitude, def.projectile.speed);
            var projectileProperties = def.projectile as ProjectilePropertiesCE;
            this.castShadow = projectileProperties.castShadow;
            this.velocity = heading;
            this.launcher = launcher;
            this.equipment = equipment;
            //For explosives/bullets, equipmentDef is important
            equipmentDef = (equipment != null) ? equipment.def : null;

            if (!def.projectile.soundAmbient.NullOrUndefined())
            {
                var info = SoundInfo.InMap(this, MaintenanceType.PerTick);
                ambientSustainer = def.projectile.soundAmbient.TrySpawnSustainer(info);
            }
            ballisticCoefficient = projectileProperties.ballisticCoefficient.RandomInRange;
            mass = projectileProperties.mass.RandomInRange;
            radius = projectileProperties.diameter.RandomInRange / 2000; // half the diameter and mm -> m
            gravity = projectileProperties.Gravity;
            initialSpeed = shotSpeed;
        }
        #endregion

        #region Raycast
        public virtual void RayCast(Thing launcher, VerbProperties verbProps, Vector2 origin, float shotAngle, float shotRotation, float shotHeight = 0f, float shotSpeed = -1f, float spreadDegrees = 0f, float aperatureSize = 0.03f, Thing equipment = null)
        {

            float magicSpreadFactor = Mathf.Sin(0.06f / 2 * Mathf.Deg2Rad) + aperatureSize;
            float magicLaserDamageConstant = 1 / (magicSpreadFactor * magicSpreadFactor * 3.14159f);

            ProjectilePropertiesCE pprops = def.projectile as ProjectilePropertiesCE;
            shotRotation = Mathf.Deg2Rad * shotRotation + (float)(3.14159 / 2.0f);
            Vector3 direction = new Vector3(Mathf.Cos(shotRotation) * Mathf.Cos(shotAngle), Mathf.Sin(shotAngle), Mathf.Sin(shotRotation) * Mathf.Cos(shotAngle));
            Vector3 origin3 = new Vector3(origin.x, shotHeight, origin.y);
            Map map = launcher.Map;
            Vector3 destination = direction * verbProps.range + origin3;
            this.shotAngle = shotAngle;
            this.shotHeight = shotHeight;
            this.shotRotation = shotRotation;
            this.launcher = launcher;
            this.origin = origin;
            this.equipment = equipment;
            equipmentDef = equipment?.def ?? null;
            Ray ray = new Ray(origin3, direction);
            var lbce = this as LaserBeamCE;
            float spreadRadius = Mathf.Sin(spreadDegrees / 2.0f * Mathf.Deg2Rad);

            LaserGunDef defWeapon = equipmentDef as LaserGunDef;
            Vector3 muzzle = ray.GetPoint((defWeapon == null ? 0.9f : defWeapon.barrelLength));
            var it_bounds = CE_Utility.GetBoundsFor(intendedTargetThing);
            for (int i = 1; i < verbProps.range; i++)
            {
                float spreadArea = (i * spreadRadius + aperatureSize) * (i * spreadRadius + aperatureSize) * 3.14159f;
                if (pprops.damageFalloff)
                {
                    lbce.DamageModifier = 1 / (magicLaserDamageConstant * spreadArea);
                }

                Vector3 tp = ray.GetPoint(i);
                if (tp.y < 0)
                {
                    destination = tp;
                    landed = true;
                    ExactPosition = tp;
                    Position = ExactPosition.ToIntVec3();
                    break;
                }
                var iv3 = tp.ToIntVec3();
                if (!iv3.InBounds(map))
                {
                    tp = ray.GetPoint(i - 1);
                    ExactPosition = tp;
                    destination = tp;
                    landed = true;
                    LastPos = destination;
                    Position = ExactPosition.ToIntVec3();

                    lbce.SpawnBeam(muzzle, destination);
                    RayCastSuppression(muzzle.ToIntVec3(), destination.ToIntVec3());
                    lbce.Impact(null, muzzle);
                    return;

                }
                foreach (Thing thing in Map.thingGrid.ThingsListAtFast(iv3))
                {
                    if (this == thing)
                    {
                        continue;
                    }
                    var bounds = CE_Utility.GetBoundsFor(thing);
                    if (!bounds.IntersectRay(ray, out var dist))
                    {
                        continue;
                    }
                    if (i < 2 && thing != intendedTargetThing)
                    {
                        continue;
                    }

                    if (thing is Plant plant)
                    {
                        if (!Rand.Chance(thing.def.fillPercent * plant.Growth))
                        {
                            continue;
                        }
                    }
                    else if (thing is Building)
                    {
                        if (!Rand.Chance(thing.def.fillPercent))
                        {
                            continue;
                        }
                    }
                    ExactPosition = tp;
                    destination = tp;
                    landed = true;
                    LastPos = destination;
                    Position = ExactPosition.ToIntVec3();

                    lbce.SpawnBeam(muzzle, destination);
                    RayCastSuppression(muzzle.ToIntVec3(), destination.ToIntVec3());

                    lbce.Impact(thing, muzzle);

                    return;

                }

            }
            if (lbce != null)
            {
                lbce.SpawnBeam(muzzle, destination);
                RayCastSuppression(muzzle.ToIntVec3(), destination.ToIntVec3());
                Destroy(DestroyMode.Vanish);
                return;
            }
        }

        protected void RayCastSuppression(IntVec3 muzzle, IntVec3 destination, Map map = null)
        {
            if (muzzle == destination)
            {
                return;
            }

            var projectilePropsCE = def.projectile as ProjectilePropertiesCE;
            if (projectilePropsCE.suppressionFactor <= 0f ||
                projectilePropsCE.airborneSuppressionFactor <= 0f)
            {
                return;
            }

            map ??= base.Map;
            foreach (Pawn pawn in muzzle.PawnsNearSegment(destination, map, SuppressionRadius, false, true).Except(muzzle.PawnsInRange(map, SuppressionRadius)))
            {
                ApplySuppression(pawn);
            }
        }
        #endregion

        #region Launch
        /// <summary>
        /// Physics-enabled Launch() method.
        /// </summary>
        /// <param name="launcher">The Thing that launched this projectile.</param>
        /// <param name="origin">The origin of the projectile (different from the launcher for e.g grenade fragments)</param>
        /// <param name="shotAngle">Angle off the ground [radians].</param>
        /// <param name="shotRotation">Rotation between shooter and destination [degrees].</param>
        /// <param name="shotHeight">The shot height, usually the max height of the non-pawn caster, a portion of the height of the pawn caster OR zero. (default: 0)</param>
        /// <param name="shotSpeed">The shot speed (default: def.projectile.speed)</param>
        /// <param name="equipment">The equipment used to fire the projectile.</param>
        /// <param name="distance">The distance to the estimated intercept point</param>
        public virtual void Launch(Thing launcher, Vector2 origin, float shotAngle, float shotRotation, float shotHeight = 0f, float shotSpeed = -1f, Thing equipment = null, float distance = -1)
        {
            this.shotAngle = shotAngle;
            this.shotHeight = shotHeight;
            this.shotRotation = shotRotation;
            this.shotSpeed = Math.Max(shotSpeed, def.projectile.speed);
            if (def.projectile is ProjectilePropertiesCE props)
            {
                this.castShadow = props.castShadow;
                this.lerpPosition = props.lerpPosition;
            }
            Launch(launcher, origin, equipment);
            this.ticksToImpact = IntTicksToImpact;
        }

        public virtual void Launch(Thing launcher, Vector2 origin, Thing equipment = null)
        {
            this.launcher = launcher;
            this.origin = origin;
            this.equipment = equipment;
            //For explosives/bullets, equipmentDef is important
            equipmentDef = (equipment != null) ? equipment.def : null;

            if (!def.projectile.soundAmbient.NullOrUndefined())
            {
                var info = SoundInfo.InMap(this, MaintenanceType.PerTick);
                ambientSustainer = def.projectile.soundAmbient.TrySpawnSustainer(info);
            }
        }
        #endregion

        #region Collisions
        public virtual void InterceptProjectile(object interceptor, Vector3 impactPosition, bool destroyCompletely = false)
        {
            ExactPosition = impactPosition;
            landed = true;
            ticksToImpact = 0;
            if (destroyCompletely)
            {
                this.Destroy(DestroyMode.Vanish);
            }
            else
            {
                this.Impact(null);
            }
        }
        public virtual void InterceptProjectile(object interceptor, Vector3 shieldPosition, float shieldRadius, bool destroyCompletely = false)
        {
            InterceptProjectile(interceptor, BlockerRegistry.GetExactPosition(OriginIV3.ToVector3(), ExactPosition, shieldPosition, shieldRadius * shieldRadius));
        }

        protected virtual (Vector3 IntersectionPos, Action OnInterception)? CheckIntercept(IEnumerable<Thing> interceptorThings)
        {
            (Vector3 IntersectionPos, Action OnInterception)? result = null;
            float distToResult = float.MaxValue;
            var newExactPos = ExactPosition;
            foreach (var interceptorThing in interceptorThings)
            {
                var interceptorComp = interceptorThing.TryGetComp<CompProjectileInterceptor>();
                if (interceptorComp == null)
                {
                    continue;
                }
                Vector3 shieldPosition = interceptorThing.Position.ToVector3ShiftedWithAltitude(0.5f);
                float radius = interceptorComp.Props.radius;
                float radiusSq = radius * radius;


                if ((newExactPos - shieldPosition).sqrMagnitude > radiusSq)
                {
                    continue;
                }
                if (!interceptorComp.Active)
                {
                    continue;
                }

                if (interceptorComp.Props.interceptGroundProjectiles && def.projectile.flyOverhead)
                {
                    continue;
                }

                if (interceptorComp.Props.interceptAirProjectiles && !def.projectile.flyOverhead)
                {
                    continue;
                }

                if ((launcher == null || !launcher.HostileTo(interceptorThing)) && !interceptorComp.debugInterceptNonHostileProjectiles && !interceptorComp.Props.interceptNonHostileProjectiles)
                {
                    continue;
                }
                if (!interceptorComp.Props.interceptOutgoingProjectiles && (shieldPosition - lastExactPos).sqrMagnitude <= radius * radius)
                {
                    continue;
                }
                if (CE_Utility.IntersectionPoint(PreLastPos, newExactPos, shieldPosition, radius, out Vector3[] sect))
                {
                    newExactPos = sect.OrderBy(x => (OriginIV3.ToVector3() - x).sqrMagnitude).First();
                    float dist = (PreLastPos - newExactPos).MagnitudeHorizontalSquared();
                    if (dist < distToResult)
                    {
                        result = (newExactPos, () => OnCompProjectileInterceptorInterception(interceptorThing, interceptorComp, newExactPos));
                        distToResult = dist;
                    }
                }

            }
            return result;

        }
        private void OnCompProjectileInterceptorInterception(Thing interceptorThing, CompProjectileInterceptor interceptorComp, Vector3 newExactPos)
        {
            landed = true;
            ExactPosition = newExactPos;
            interceptorComp.lastInterceptAngle = lastExactPos.AngleToFlat(interceptorThing.TrueCenter());
            interceptorComp.lastInterceptTicks = Find.TickManager.TicksGame;

            var projectileProperties = def.projectile as ProjectilePropertiesCE;
            var areWeLucky = Rand.Chance(projectileProperties?.empShieldBreakChance ?? 0);
            if (areWeLucky && interceptorComp.Props.disarmedByEmpForTicks > 0)
            {
                // If the chance check for this EMP projectile succeeds, break the shield using the appropriate damage type
                // (primary if the primary damage is EMP itself and secondary if EMP damage is only a secondary effect.)
                // Note that empShieldBreakChance defaults to 1 even for non-EMP projectiles, so a non-EMP projectile
                // may still technically pass the chance check.
                var empDamageDef = def.projectile.damageDef == DamageDefOf.EMP
                                   ? def.projectile.damageDef
                                   : projectileProperties?.secondaryDamage?.Select(sd => sd.def).FirstOrDefault(sdDef => sdDef == DamageDefOf.EMP);

                if (empDamageDef != null)
                {
                    interceptorComp.BreakShieldEmp(new DamageInfo(empDamageDef, empDamageDef.defaultDamage));

                    // Ensure we reset hit points for Biotech's new shields if broken by EMP
                    interceptorComp.currentHitPoints = 0;
                    interceptorComp.nextChargeTick = Find.TickManager.TicksGame;
                }
            }

            // Handle Biotech's new shields used e.g. on the Centurion mech, which, unlike mech cluster shields, can only take
            // a finite amount of damage before breaking.
            // This simply mirrors the corresponding vanilla logic - we apply the incoming damage from our projectile to the shield
            // and break it if we manage to decrease its hitpoints to zero or lower.
            if (interceptorComp.currentHitPoints > 0)
            {
                interceptorComp.currentHitPoints -= Mathf.FloorToInt(this.DamageAmount);

                if (interceptorComp.currentHitPoints <= 0)
                {
                    interceptorComp.currentHitPoints = 0;
                    interceptorComp.nextChargeTick = Find.TickManager.TicksGame;
                    interceptorComp.BreakShieldHitpoints(new DamageInfo(projectileProperties.damageDef, this.DamageAmount));
                    this.Impact(null);
                    return;
                }
            }

            Effecter eff = new Effecter(EffecterDefOf.Interceptor_BlockedProjectile);
            eff.Trigger(new TargetInfo(Position, interceptorThing.Map, false), TargetInfo.Invalid);
            eff.Cleanup();
            this.Impact(null);
        }
        //Removed minimum collision distance
        protected bool CheckForCollisionBetween()
        {
            (Vector3 IntersectionPos, Action OnInterception)? interception = null;
            float dist = float.MaxValue;
            bool collided = false;
            Map localMap = this.Map; // Saving the map in case CheckCellForCollision->...->Impact destroys the projectile, thus setting this.Map to null
            var lastPosIV3 = LastPos.ToIntVec3();
            var newPosIV3 = ExactPosition.ToIntVec3();

            List<Thing> list = base.Map.listerThings.ThingsInGroup(ThingRequestGroup.ProjectileInterceptor);
            var newInterceptor = CheckIntercept(list);
            CheckNewInterceptor(newInterceptor);

            #region Sanity checks
            if (ticksToImpact < 0 || def.projectile.flyOverhead)
            {
                return false;
            }

            if (!lastPosIV3.InBounds(Map) || !newPosIV3.InBounds(Map))
            {
                return false;
            }

            if (Controller.settings.DebugDrawInterceptChecks)
            {
                Map.debugDrawer.FlashLine(lastPosIV3, newPosIV3);
            }
            #endregion
            newInterceptor = BlockerRegistry.CheckForCollisionBetweenCallback(this, PreLastPos, ExactPosition);
            CheckNewInterceptor(newInterceptor);
            if (interception.HasValue)
            {
                newPosIV3 = interception.Value.IntersectionPos.ToIntVec3();
            }
            // Iterate through all cells between the last and the new position
            // INCLUDING[!!!] THE LAST AND NEW POSITIONS!
            var cells = GenSight.PointsOnLineOfSight(lastPosIV3, newPosIV3).Union(new[] { lastPosIV3, newPosIV3 }).Distinct().OrderBy(x => (x.ToVector3Shifted() - LastPos).MagnitudeHorizontalSquared());
            //Order cells by distance from the last position
            foreach (var cell in cells)
            {
                newInterceptor = PossibleTargetForCell(cell);

                if (Controller.settings.DebugDrawInterceptChecks)
                {
                    Map.debugDrawer.FlashCell(cell, 1, "o");
                }
                if (CheckNewInterceptor(newInterceptor))//if we found any target close, we don't need to check other
                {
                    break;
                }
            }
            if (interception.HasValue)
            {
                interception.Value.OnInterception();
                newPosIV3 = interception.Value.IntersectionPos.ToIntVec3();
                collided = true;
            }

            // Apply suppression. The height here is NOT that of the bullet in CELL,
            // it is the height at the END OF THE PATH. This is because SuppressionRadius
            // is not considered an EXACT limit.
            if (ExactPosition.y <= SuppressionRadius)
            {
                RayCastSuppression(lastPosIV3, newPosIV3, localMap);
            }
            return collided;
            bool CheckNewInterceptor((Vector3 IntersectionPos, Action OnInterception)? newValue)
            {
                float newDist;
                if (newValue.HasValue && (newDist = (LastPos - newValue.Value.IntersectionPos).MagnitudeHorizontalSquared()) < dist)
                {
                    interception = newInterceptor;
                    dist = newDist;
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Checks whether a collision occurs along flight path within this cell.
        /// </summary>
        /// <param name="cell">Where to check for collisions in</param>
        /// <returns>True if collision occured, false otherwise</returns>
        protected (Vector3 IntersectionPos, Action OnIntersection)? PossibleTargetForCell(IntVec3 cell)
        {
            Vector3 roofIntersectionPos;
            bool roofChecked = false;
            //If the last position is above the wallCollisionHeight, we should check for roof intersections first
            if (LastPos.y > CollisionVertical.WallCollisionHeight)
            {
                if (CanCollideWithRoof(cell, out roofIntersectionPos))
                {
                    return (roofIntersectionPos, () => CollideWithRoof(roofIntersectionPos));

                }
                roofChecked = true;
            }
            var mainThingList = new List<Thing>(Map.thingGrid.ThingsListAtFast(cell)).Where(t => t is Pawn || t.def.Fillage != FillCategory.None).ToList();

            //Find pawns in adjacent cells and append them to main list
            var adjList = new List<IntVec3>();
            var rot4 = Rot4.FromAngleFlat(shotRotation);
            if (rot4.rotInt > 1)
            {
                //For some reason south and west returns incorrect adjacent cells collection
                rot4 = rot4.Opposite;
            }
            adjList.AddRange(GenAdj.CellsAdjacentCardinal(cell, rot4, new IntVec2(collisionCheckSize, 0)).ToList());
            //Iterate through adjacent cells and find all the pawns
            foreach (var curCell in adjList)
            {
                if (curCell != cell && curCell.InBounds(Map))
                {
                    mainThingList.AddRange(Map.thingGrid.ThingsListAtFast(curCell)
                                           .Where(x => x is Pawn));

                    if (Controller.settings.DebugDrawInterceptChecks)
                    {
                        Map.debugDrawer.FlashCell(curCell, 0.7f);
                    }
                }
            }
            (Vector3 IntersectionPos, Action OnIntersection)? result = null;
            float resultDist = float.MaxValue;
            float dist;
            foreach (var thing in mainThingList.Distinct().Where(x => !(x is ProjectileCE)).OrderBy(x => (x.DrawPos - LastPos).sqrMagnitude))
            {
                if ((thing == launcher || thing == mount) && !canTargetSelf)
                {
                    continue;
                }

                // Check for collision
                if (thing == intendedTargetThing || def.projectile.alwaysFreeIntercept || thing.Position.DistanceTo(OriginIV3) >= minCollisionDistance)
                {
                    if (!CanCollideWith(thing, out _, out var thingIntersectionPoint))
                    {
                        continue;
                    }

                    dist = (LastPos - thingIntersectionPoint).MagnitudeHorizontalSquared();
                    if (dist < resultDist)
                    {
                        resultDist = dist;
                        result = (thingIntersectionPoint, () => TryCollideWith(thing));
                    }
                }
            }

            //Finally check for intersecting with a roof (again).
            if (!roofChecked && CanCollideWithRoof(cell, out roofIntersectionPos))
            {
                dist = (LastPos - roofIntersectionPos).MagnitudeHorizontalSquared();
                if (dist < resultDist)
                {
                    result = (roofIntersectionPos, () => CollideWithRoof(roofIntersectionPos));
                }
            }
            return result;
        }

        protected bool CanCollideWithRoof(IntVec3 cell, out Vector3 point)
        {
            point = Vector3.negativeInfinity;
            if (!cell.Roofed(Map))
            {
                return false;
            }

            var bounds = CE_Utility.GetBoundsFor(cell, cell.GetRoof(Map));

            float dist;
            if (!bounds.IntersectRay(ShotLine, out dist))
            {
                return false;
            }
            if (dist * dist > ExactMinusLastPos.sqrMagnitude)
            {
                return false;
            }
            point = ShotLine.GetPoint(dist);
            return true;
        }
        public void CollideWithRoof(Vector3 point)
        {
            ExactPosition = point;
            landed = true;

            if (Controller.settings.DebugDrawInterceptChecks)
            {
                MoteMakerCE.ThrowText(point, Map, "x", Color.red);
            }

            Impact(null);
        }
        protected bool CanCollideWith(Thing thing, out float dist, out Vector3 point)
        {
            dist = -1f;
            point = Vector3.negativeInfinity;
            if (globalTargetInfo.IsValid)
            {
                return false;
            }
            if (thing == launcher && !canTargetSelf)
            {
                return false;
            }

            var bounds = CE_Utility.GetBoundsFor(thing);
            if (!bounds.IntersectRay(ShotLine, out dist))
            {
                return false;
            }
            if (dist * dist > ExactMinusLastPos.sqrMagnitude)
            {
                return false;
            }
            point = ShotLine.GetPoint(dist);
            return true;
        }
        /// <summary>
        /// Tries to impact the thing based on whether it intersects the given flight path. Trees have RNG chance to not collide even on intersection.
        /// </summary>
        /// <param name="thing">What to impact</param>
        /// <returns>True if impact occurred, false otherwise</returns>
        protected bool TryCollideWith(Thing thing)
        {

            if (!CanCollideWith(thing, out var dist, out var point))
            {
                return false;
            }
            // Trees and bushes have RNG chance to collide
            if (thing is Plant)
            {
                //Prevents trees near the shooter (e.g the shooter's cover) to be hit
                var accuracyFactor = def.projectile.alwaysFreeIntercept ? 1 : (thing.Position - OriginIV3).LengthHorizontal / 40 * AccuracyFactor;
                var chance = thing.def.fillPercent * accuracyFactor;
                if (Controller.settings.DebugShowTreeCollisionChance)
                {
                    MoteMakerCE.ThrowText(thing.Position.ToVector3Shifted(), thing.Map, chance.ToString());
                }
                if (!Rand.Chance(chance))
                {
                    return false;
                }
            }

            if (!point.InBounds(Map))
            {
                if (OffMapOrigin)
                {
                    landed = true;
                    Destroy(DestroyMode.Vanish);
                    return true;
                }
                else
                {
                    Log.Error("TryCollideWith out of bounds point from ShotLine: obj " + thing.ThingID + ", proj " + ThingID + ", dist " + dist + ", point " + point);
                }
            }

            if (BlockerRegistry.BeforeCollideWithCallback(this, thing))
            {
                return true;
            }
            ExactPosition = point;
            landed = true;
            if (Controller.settings.DebugDrawInterceptChecks)
            {
                MoteMakerCE.ThrowText(thing.Position.ToVector3Shifted(), thing.Map, "x", Color.red);
            }

            Impact(thing);
            return true;
        }
        #endregion

        /// <summary>
        /// Applies suppression based off of damage and suppression multiplier to pawns which don't have a shield belt or one is broken;
        /// </summary>
        /// <param name="pawn">Which pawn to suppress</param>
        /// <param name="suppressionMultiplier">How much to multiply the projectile's damage by before using it as suppression</param>
        protected void ApplySuppression(Pawn pawn, float suppressionMultiplier = 1f)
        {
            var propsCE = def.projectile as ProjectilePropertiesCE;

            if (propsCE.suppressionFactor <= 0f || (!landed && propsCE.airborneSuppressionFactor <= 0f))
            {
                return;
            }

            CompShield shield = pawn.TryGetComp<CompShield>();
            if (pawn.RaceProps.Humanlike)
            {
                // check for shield user

                var wornApparel = pawn.apparel.WornApparel;
                for (var i = 0; i < wornApparel.Count; i++)
                {
                    var personalShield = wornApparel[i].TryGetComp<CompShield>();
                    if (personalShield != null)
                    {
                        shield = personalShield;
                        break;
                    }
                }
            }
            //Add suppression
            var compSuppressable = pawn.TryGetComp<CompSuppressable>();
            if (compSuppressable != null
                    && pawn.Faction != launcher?.Faction
                    && (shield == null || shield.ShieldState == ShieldState.Resetting)
                    && !compSuppressable.IgnoreSuppresion(OriginIV3))
            {
                suppressionAmount = def.projectile.damageAmountBase * suppressionMultiplier;

                suppressionAmount *= propsCE.suppressionFactor;
                if (!landed)
                {
                    suppressionAmount *= propsCE.airborneSuppressionFactor;
                }

                var explodeRadius = propsCE.explosionRadius;
                if (explodeRadius == 0f)
                {
                    var comp = this.TryGetComp<CompExplosiveCE>()?.props as CompProperties_ExplosiveCE;
                    if (comp != null)
                    {
                        explodeRadius = comp.explosiveRadius;
                        suppressionAmount = comp.damageAmountBase;
                    }
                }

                if (explodeRadius == 0f)
                {
                    var penetrationAmount = propsCE?.armorPenetrationSharp ?? 0f;
                    var armorMod = penetrationAmount <= 0 ? 0 : 1 - Mathf.Clamp(pawn.GetStatValue(CE_StatDefOf.AverageSharpArmor) * 0.5f / penetrationAmount, 0, 1);
                    suppressionAmount *= armorMod;
                }
                else
                {
                    // Larger suppression amount at distances compared to linear interpolation.
                    var dPosX = ExactPosition.x - pawn.DrawPos.x;
                    var dPosZ = ExactPosition.z - pawn.DrawPos.z;
                    // Affected by the ratio of distance from the explosion/projectile to the max suppression radius raised to the power of two.
                    var totalRadius = explodeRadius + SuppressionRadius;
                    var distanceFactor = Mathf.Clamp01(1f - (dPosX * dPosX + dPosZ * dPosZ) / (totalRadius * totalRadius));
                    suppressionAmount *= distanceFactor;
                }
                compSuppressable.AddSuppression(suppressionAmount, OriginIV3);
            }
        }

        // If anyone wants to override how projectiles move, this can be made virtual.
        // For now, it is non-virtual for performance.
        protected Vector3 MoveForward()
        {
            Vector3 curPosition = ExactPosition;
            float sr = shotRotation * Mathf.Deg2Rad + 3.14159f / 2.0f;
            if (!kinit)
            {
                kinit = true;
                var projectileProperties = def.projectile as ProjectilePropertiesCE;
                ballisticCoefficient = projectileProperties.ballisticCoefficient.RandomInRange;
                mass = projectileProperties.mass.RandomInRange;
                radius = projectileProperties.diameter.RandomInRange / 2000;
                gravity = projectileProperties.Gravity;
                float sspt = shotSpeed / GenTicks.TicksPerRealSecond;
                velocity = new Vector3(Mathf.Cos(sr) * Mathf.Cos(shotAngle) * sspt, Mathf.Sin(shotAngle) * sspt, Mathf.Sin(sr) * Mathf.Cos(shotAngle) * sspt);
                initialSpeed = sspt;
            }
            Vector3 newPosition = curPosition + velocity;
            Accelerate();
            return newPosition;
        }

        // This can also be made virtual, and would be the ideal entry point for guided ammunition and rockets.
        protected void Accelerate()
        {
            float crossSectionalArea = radius;
            crossSectionalArea *= crossSectionalArea * 3.14159f;
            // 2.5f is half the mass of 1m² x 1cell of air.
            var q = 2.5f * shotSpeed * shotSpeed;
            var dragForce = q * crossSectionalArea / ballisticCoefficient;
            // F = mA
            // A = F / m
            var a = (float)((-dragForce / (float)mass));
            var normalized = velocity.normalized;
            velocity.x += a * normalized.x;
            velocity.y += a * normalized.y - (float)(1 / ballisticCoefficient) * (float)gravity / GenTicks.TicksPerRealSecond;
            velocity.z += a * normalized.z;
            shotSpeed = velocity.magnitude;
        }

        #region Tick/Draw
        public override void Tick()
        {
#if DEBUG
            CleanUpDebug(Map);
#endif
            base.Tick();
            if (landed)
            {
                return;
            }
            LastPos = ExactPosition;
            ticksToImpact--;
            flightTicks++;
            Vector3 nextPosition;
            if (lerpPosition)
            {
                var v = Vec2Position();
                nextPosition = new Vector3(v.x, Height, v.y);
            }
            else
            {
                nextPosition = MoveForward();
            }
            if (!nextPosition.InBounds(Map))
            {
                if (globalTargetInfo.IsValid)
                {
                    TravelingShell shell = (TravelingShell)WorldObjectMaker.MakeWorldObject(CE_WorldObjectDefOf.TravelingShell);
                    if (launcher?.Faction != null)
                    {
                        shell.SetFaction(launcher.Faction);
                    }
                    shell.tileInt = Map.Tile;
                    shell.SpawnSetup();
                    Find.World.worldObjects.Add(shell);
                    shell.launcher = launcher;
                    shell.equipmentDef = equipmentDef;
                    shell.globalSource = new GlobalTargetInfo(OriginIV3, Map);
                    shell.globalSource.tileInt = Map.Tile;
                    shell.globalSource.mapInt = Map;
                    shell.globalSource.worldObjectInt = Map.Parent;
                    shell.shellDef = def;
                    shell.globalTarget = globalTargetInfo;
                    if (!shell.TryTravel(Map.Tile, globalTargetInfo.Tile))
                    {
                        Log.Error($"CE: Travling shell {this.def} failed to launch!");
                        shell.Destroy();
                    }
                }
                Destroy();
                return;
            }
            ExactPosition = nextPosition;
            if (CheckForCollisionBetween())
            {
                return;
            }
            Position = nextPosition.ToIntVec3();
            if (globalTargetInfo.IsValid)
            {
                return;
            }
            if (ticksToImpact == 60 && Find.TickManager.CurTimeSpeed == TimeSpeed.Normal && def.projectile.soundImpactAnticipate != null)
            {
                def.projectile.soundImpactAnticipate.PlayOneShot(this);
            }
            //TODO : It appears that the final steps in the arc (past ticksToImpact == 0) don't CheckForCollisionBetween.
            if (ticksToImpact <= 0 || nextPosition.y <= 0f)
            {
                ImpactSomething();
                return;
            }
            if (ambientSustainer != null)
            {
                ambientSustainer.Maintain();
            }

            if (def.HasModExtension<TrailerProjectileExtension>())
            {
                var trailer = def.GetModExtension<TrailerProjectileExtension>();
                if (trailer != null)
                {
                    if (ticksToImpact % trailer.trailerMoteInterval == 0)
                    {
                        for (int i = 0; i < trailer.motesThrown; i++)
                        {
                            TrailThrower.ThrowSmoke(DrawPos, trailer.trailMoteSize, Map, trailer.trailMoteDef);
                        }
                    }
                }
            }
            float distToOrigin = originInt.DistanceTo(positionInt);
            float dangerFactor = (def.projectile as ProjectilePropertiesCE).dangerFactor;
            if (dangerFactor > 0f && nextPosition.y < CollisionVertical.WallCollisionHeight && distToOrigin > 3)
            {
                DangerTracker?.Notify_BulletAt(Position, def.projectile.damageAmountBase * dangerFactor);
            }
        }

        /// <summary>
        /// Draws projectile if at least a tick away from caster (or always if no caster)
        /// </summary>
        public override void Draw()
        {
            if (FlightTicks == 0 && launcher != null && launcher is Pawn)
            {
                //TODO: Draw at the end of the barrel on the pawn
            }
            else
            {
                //Projectile
                //Graphics.DrawMesh(MeshPool.plane10, DrawPos, DrawRotation, def.DrawMatSingle, 0);
                Graphics.DrawMesh(MeshPool.GridPlane(def.graphicData.drawSize), DrawPos, DrawRotation, def.DrawMatSingle, 0);

                //Shadow
                if (castShadow)
                {
                    //TODO : EXPERIMENTAL Add edifice height
                    var shadowPos = new Vector3(ExactPosition.x,
                                                def.Altitude - 0.01f,
                                                ExactPosition.z - Mathf.Lerp(shotHeight, 0f, fTicks / StartingTicksToImpact));
                    //EXPERIMENTAL: + (new CollisionVertical(ExactPosition.ToIntVec3().GetEdifice(Map))).Max);

                    //TODO : Vary ShadowMat plane
                    //Graphics.DrawMesh(MeshPool.plane08, shadowPos, ExactRotation, ShadowMaterial, 0);
                    Graphics.DrawMesh(MeshPool.GridPlane(def.graphicData.drawSize), shadowPos, ExactRotation, ShadowMaterial, 0);
                }

                Comps_PostDraw();
            }
        }
        #endregion

        #region Impact
        //Modified collision with downed pawns
        public void ImpactSomething()
        {
            if (BlockerRegistry.ImpactSomethingCallback(this, launcher))
            {
                return;
            }
            var pos = ExactPosition.ToIntVec3();

            //Not modified, just mortar code
            if (def.projectile.flyOverhead)
            {
                var roofDef = Map.roofGrid.RoofAt(pos);
                if (roofDef != null)
                {
                    if (roofDef.isThickRoof)
                    {
                        def.projectile.soundHitThickRoof.PlayOneShot(new TargetInfo(pos, Map));
                        Destroy();
                        return;
                    }
                    if (pos.GetEdifice(Map) == null || pos.GetEdifice(Map).def.Fillage != FillCategory.Full)
                    {
                        RoofCollapserImmediate.DropRoofInCells(pos, Map);
                    }
                }
            }

            // FIXME : Early opt-out
            Thing thing = pos.GetFirstPawn(Map);
            if (thing != null && TryCollideWith(thing))
            {
                return;
            }

            var list = Map.thingGrid.ThingsListAt(pos).Where(t => t is Pawn || t.def.Fillage != FillCategory.None).ToList();
            if (list.Count > 0)
            {
                foreach (var thing2 in list)
                {
                    if (TryCollideWith(thing2))
                    {
                        return;
                    }
                }
            }

            ExactPosition = ExactPosition;
            landed = true;
            Impact(null);
        }

        public virtual void Impact(Thing hitThing)
        {
            //if(cameraShakingInit > 0f && Find.CameraDriver != null)
            //{
            //    Find.CameraDriver.shaker.DoShake(cameraShakingInit);
            //}
            if (Controller.settings.EnableExtraEffects)
            {
                ImpactFleckThrower.ThrowFleck(ExactPosition,
                        Position,
                        Map,
                        def.projectile as ProjectilePropertiesCE,
                        def, hitThing, shotRotation);
            }
            var ignoredThings = new List<Thing>();

            //Spawn things from preExplosionSpawnThingDef != null
            if (Position.IsValid
                    && def.projectile.preExplosionSpawnChance > 0
                    && def.projectile.preExplosionSpawnThingDef != null
                    && (Controller.settings.EnableAmmoSystem || !(def.projectile.preExplosionSpawnThingDef is AmmoDef))
                    && Rand.Value < def.projectile.preExplosionSpawnChance)
            {
                var thingDef = def.projectile.preExplosionSpawnThingDef;

                if (thingDef.IsFilth && Position.Walkable(Map))
                {
                    FilthMaker.TryMakeFilth(Position, Map, thingDef);
                }
                else if (Controller.settings.ReuseNeolithicProjectiles)
                {
                    var reusableAmmo = ThingMaker.MakeThing(thingDef);
                    reusableAmmo.stackCount = 1;
                    reusableAmmo.SetForbidden(true, false);
                    GenPlace.TryPlaceThing(reusableAmmo, Position, Map, ThingPlaceMode.Near);
                    LessonAutoActivator.TeachOpportunity(CE_ConceptDefOf.CE_ReusableNeolithicProjectiles, reusableAmmo, OpportunityType.GoodToKnow);
                    ignoredThings.Add(reusableAmmo);
                }
            }

            var explodePos = ExactPosition;

            if (!explodePos.ToIntVec3().IsValid)
            {
                Destroy();
                return;
            }

            if (def.projectile.explosionEffect != null)
            {
                Effecter effecter = def.projectile.explosionEffect.Spawn();
                effecter.Trigger(new TargetInfo(explodePos.ToIntVec3(), Map, false), new TargetInfo(explodePos.ToIntVec3(), Map, false));
                effecter.Cleanup();
            }
            ProjectilePropertiesCE projectileCE = def.projectile as ProjectilePropertiesCE;
            float effectScale = projectileCE.detonateEffectsScaleOverride > 0 ? projectileCE.detonateEffectsScaleOverride : projectileCE.explosionRadius * 2;
            if (projectileCE.detonateMoteDef != null)
            {
                MoteMaker.MakeStaticMote(DrawPos, Map, CE_ThingDefOf.Mote_BigExplode, effectScale);
            }
            if (projectileCE.detonateFleckDef != null)
            {
                FleckCreationData dataStatic = FleckMaker.GetDataStatic(DrawPos, MapHeld, projectileCE.detonateFleckDef, effectScale);
                MapHeld.flecks.CreateFleck(dataStatic);
            }
            var projectilePropsCE = (def.projectile as ProjectilePropertiesCE);

            var explodingComp = this.TryGetComp<CompExplosiveCE>();

            if (explodingComp == null)
            {
                foreach (var comp in GetComps<CompFragments>())
                {
                    comp.Throw(explodePos, Map, launcher);
                }
            }

            //If the comp exists, it'll already call CompFragments
            if (explodingComp != null || def.projectile.explosionRadius > 0f)
            {
                float explosionSuppressionRadius = SuppressionRadius + (def.projectile.applyDamageToExplosionCellsNeighbors ? 1.5f : 0f);
                //Handle anything explosive
                if (hitThing is Pawn pawn && pawn.Dead)
                {
                    ignoredThings.Add(pawn.Corpse);
                }

                var suppressThings = new List<Pawn>();
                float dangerAmount = 0f;
                var dir = new float?(origin.AngleTo(Vec2Position()));

                // Opt-out for things without explosionRadius
                if (def.projectile.explosionRadius > 0f)
                {
                    GenExplosionCE.DoExplosion(
                        explodePos.ToIntVec3(),
                        Map,
                        def.projectile.explosionRadius,
                        def.projectile.damageDef,
                        launcher,
                        Mathf.FloorToInt(DamageAmount),
                        def.projectile.GetExplosionArmorPenetration(),
                        def.projectile.soundExplode,
                        equipmentDef,
                        def,
                        intendedTarget: null,
                        def.projectile.postExplosionSpawnThingDef,
                        def.projectile.postExplosionSpawnChance,
                        def.projectile.postExplosionSpawnThingCount,
                        def.projectile.postExplosionGasType,
                        def.projectile.applyDamageToExplosionCellsNeighbors,
                        def.projectile.preExplosionSpawnThingDef,
                        def.projectile.preExplosionSpawnChance,
                        def.projectile.preExplosionSpawnThingCount,
                        def.projectile.explosionChanceToStartFire,
                        def.projectile.explosionDamageFalloff,
                        dir,
                        ignoredThings,
                        postExplosionSpawnThingDefWater: def.projectile.postExplosionSpawnThingDefWater,
                        screenShakeFactor: def.projectile.screenShakeFactor,
                        height: explodePos.y);

                    dangerAmount = def.projectile.damageAmountBase;

                    // Apply suppression around impact area
                    if (explodePos.y < SuppressionRadius)
                    {
                        explosionSuppressionRadius += def.projectile.explosionRadius;

                        if (projectilePropsCE.suppressionFactor > 0f)
                        {
                            suppressThings.AddRange(explodePos.ToIntVec3().PawnsInRange(Map,
                                                    explosionSuppressionRadius));
                        }
                    }
                }
                if (explodingComp != null)
                {
                    dangerAmount = (explodingComp.props as CompProperties_ExplosiveCE).damageAmountBase;
                    explodingComp.Explode(this, explodePos, Map, 1f, dir, ignoredThings);

                    if (explodePos.y < SuppressionRadius)
                    {
                        explosionSuppressionRadius += (explodingComp.props as CompProperties_ExplosiveCE).explosiveRadius;

                        if (projectilePropsCE.suppressionFactor > 0f)
                        {
                            suppressThings.AddRange(explodePos.ToIntVec3().PawnsInRange(Map,
                                                    SuppressionRadius + (explodingComp.props as CompProperties_ExplosiveCE).explosiveRadius));
                        }
                    }
                }

                foreach (var thing in suppressThings)
                {
                    ApplySuppression(thing);
                }

                if (projectilePropsCE.dangerFactor > 0f)
                {
                    DangerTracker.Notify_DangerRadiusAt(Position,
                                                        explosionSuppressionRadius - SuppressionRadius,
                                                        dangerAmount * projectilePropsCE.dangerFactor);
                }
            }
            else
            {
                if (projectilePropsCE.dangerFactor > 0f)
                {
                    DangerTracker?.Notify_BulletAt(ExactPosition.ToIntVec3(),
                                                   def.projectile.damageAmountBase * projectilePropsCE.dangerFactor);
                }
            }

            Destroy();
        }
        #endregion

        #region Ballistics
        /// <summary>
        /// Calculated rounding to three decimales the output of h0 + v * sin(a0) * t - g/2 * t^2 with {h0 -> shotHeight, v -> shotSpeed, a0 -> shotAngle, t -> ticks/GenTicks.TicksPerRealSecond, g -> GravityFactor}. Called roughly each tick for impact checks and for drawing.
        /// </summary>
        /// <param name="ticks">Integer ticks, since the only time value which is not an integer (accessed by StartingTicksToImpact) has height zero by definition.</param>
        /// <returns>Projectile height at time ticks in ticks.</returns>
        private float GetHeightAtTicks(int ticks)
        {
            var seconds = ((float)ticks) / GenTicks.TicksPerRealSecond;
            return (float)Math.Round(shotHeight + shotSpeed * Mathf.Sin(shotAngle) * seconds - (GravityFactor * seconds * seconds) / 2f, 3);
        }

        /// <summary>
        /// Calculates the time in seconds the arc characterized by <i>angle</i>, <i>shotHeight</i> takes to traverse at speed <i>velocity</i> - e.g until the height reaches zero. Does not take into account air resistance.
        /// </summary>
        /// <param name="velocity">Projectile velocity in cells per second.</param>
        /// <param name="angle">Shot angle in radians off the ground.</param>
        /// <param name="shotHeight">Height from which the projectile is fired in vertical cells.</param>
        /// <returns>Time in seconds that the projectile will take to traverse the given arc.</returns>
        protected float GetFlightTime()
        {
            //Calculates quadratic formula (g/2)t^2 + (-v_0y)t + (y-y0) for {g -> gravity, v_0y -> vSin, y -> 0, y0 -> shotHeight} to find t in fractional ticks where height equals zero.
            return (Mathf.Sin(shotAngle) * shotSpeed + Mathf.Sqrt(Mathf.Pow(Mathf.Sin(shotAngle) * shotSpeed, 2f) + 2f * GravityFactor * shotHeight)) / GravityFactor;
        }

        /// <summary>
        /// Calculates the range reachable with a projectile of speed <i>velocity</i> fired at <i>angle</i> from height <i>shotHeight</i>. Does not take into account air resistance.
        /// </summary>
        /// <param name="velocity">Projectile velocity in cells per second.</param>
        /// <param name="angle">Shot angle in radians off the ground.</param>
        /// <param name="shotHeight">Height from which the projectile is fired in vertical cells.</param>
        /// <returns>Distance in cells that the projectile will fly at the given arc.</returns>
        private float DistanceTraveled => CE_Utility.MaxProjectileRange(shotHeight, shotSpeed, shotAngle, GravityFactor);

        /// <summary>
        /// Calculates the shot angle necessary to reach <i>range</i> with a projectile of speed <i>velocity</i> at a height difference of <i>heightDifference</i>, returning either the upper or lower arc in radians. Does not take into account air resistance.
        /// </summary>
        /// <param name="velocity">Projectile velocity in cells per second.</param>
        /// <param name="range">Cells between shooter and target.</param>
        /// <param name="heightDifference">Difference between initial shot height and target height in vertical cells.</param>
        /// <param name="flyOverhead">Whether to take the lower (False) or upper (True) arc angle.</param>
        /// <returns>Arc angle in radians off the ground.</returns>
        public static float GetShotAngle(float velocity, float range, float heightDifference, bool flyOverhead, float gravity)
        {
            float squareRootCheck = Mathf.Sqrt(Mathf.Pow(velocity, 4f) - gravity * (gravity * Mathf.Pow(range, 2f) + 2f * heightDifference * Mathf.Pow(velocity, 2f)));
            if (float.IsNaN(squareRootCheck))
            {
                //Target is too far to hit with given velocity/range/gravity params
                //set firing angle for maximum distance
                Log.Warning("[CE] Tried to fire projectile to unreachable target cell, truncating to maximum distance.");
                return 45.0f * Mathf.Deg2Rad;
            }
            return Mathf.Atan((Mathf.Pow(velocity, 2f) + (flyOverhead ? 1f : -1f) * squareRootCheck) / (gravity * range));
        }
        #endregion

        protected static Material[] GetShadowMaterial(Graphic_Collection g)
        {
            var collection = g.subGraphics;
            var shadows = collection.Select(item => item.GetColoredVersion(ShaderDatabase.Transparent, Color.black, Color.black).MatSingle).ToArray();

            return shadows;
        }
#if DEBUG

        protected static int lastDebugCleanUpTick = -1;
        protected static void CleanUpDebug(Map map)
        {
            var currentTick = Find.TickManager.TicksGame;
            if (currentTick == lastDebugCleanUpTick)
            {
                return;
            }
            map?.debugDrawer.debugCells.Clear();
            map?.debugDrawer.debugLines.Clear();
            map?.debugDrawer.DebugDrawerUpdate();
            lastDebugCleanUpTick = currentTick;
        }
#endif

        #endregion
    }
}
