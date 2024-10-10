﻿using HarmonyLib;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;
using VFECore;
namespace CombatExtended.Compatibility
{
    public class VanillaExpandedFramework : IPatch
    {
        const string ModName = "Vanilla Expanded Framework";
        bool IPatch.CanInstall()
        {
            return ModLister.HasActiveModWithName(ModName);
        }

        void IPatch.Install()
        {
            BlockerRegistry.RegisterCheckForCollisionBetweenCallback(CheckIntercept);
            BlockerRegistry.RegisterShieldZonesCallback(ShieldZonesCallback);
        }

        private IEnumerable<IEnumerable<IntVec3>> ShieldZonesCallback(Thing pawnToSuppress)
        {
            IEnumerable<CompShieldField> interceptors = CompShieldField.ListerShieldGensActiveIn(pawnToSuppress.Map).ToList();
            List<IEnumerable<IntVec3>> result = new List<IEnumerable<IntVec3>>();
            if (!interceptors.Any())
            {
                return result;
            }
            foreach (var interceptor in interceptors)
            {
                if (!interceptor.CanFunction)
                {
                    continue;
                }
                result.Add(GenRadial.RadialCellsAround(interceptor.HostThing.Position, interceptor.ShieldRadius, true));
            }
            return result;
        }

        private static IEnumerable<(Vector3, Action)> CheckIntercept(ProjectileCE projectile, Vector3 lastExactPos, Vector3 newExactPos)
        {
            List<(Vector3, Action)> result = new List<(Vector3, Action)>();
            if (projectile.def.projectile.flyOverhead)
            {
                return result;
            }
            IEnumerable<CompShieldField> interceptors = CompShieldField.ListerShieldGensActiveIn(projectile.Map).ToList();
            if (!interceptors.Any())
            {
                return result;
            }
            var def = projectile.def;
            foreach (ThingComp comp in interceptors)
            {
                var interceptor = comp as CompShieldField;
                if (!interceptor.CanFunction)
                {
                    continue;
                }
                Vector3 shieldPosition = interceptor.HostThing.Position.ToVector3Shifted().Yto0();
                float radius = interceptor.ShieldRadius;

                Vector3[] intersectionPoints;
                if (!CE_Utility.IntersectionPoint(lastExactPos, newExactPos, shieldPosition, radius, out intersectionPoints, false))
                {
                    continue;
                }
                var exactPosition = intersectionPoints.OrderBy(x => (projectile.OriginIV3.ToVector3() - x).sqrMagnitude).First();


                result.Add((exactPosition, () => OnIntercepted(projectile, comp, exactPosition)));
            }
            return result;
        }
        private static void OnIntercepted(ProjectileCE projectile, ThingComp comp, Vector3 newExactPos)
        {
            var interceptor = comp as CompShieldField;
            projectile.ExactPosition = newExactPos;
            projectile.landed = true;
            projectile.InterceptProjectile(interceptor.HostThing, projectile.ExactPosition, true);
            interceptor.AbsorbDamage(projectile.DamageAmount, projectile.def.projectile.damageDef, projectile.launcher);
        }

    }

}
