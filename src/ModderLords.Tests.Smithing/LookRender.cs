using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace ModderLords.Tests.Smithing;

/// <summary>
/// One armor piece shown on its own, the way MS2's designer previews it: the game's inventory mesh for the item (the
/// call MS2's dye renderer hooks, so the dye shows) on a bare entity with the game's plain body part under it, as the
/// native item tableau builds it (ItemTableau.RefreshItemTableau). Gloves get the tableau's hand pose; everything else
/// stands in its bind pose. The entity hangs in the open air above the scene, lit full bright: a light at the camera and
/// six shadowless fill lights round it (front, back, sides, above, below), so no side falls into shadow and a dye
/// region's edge shows wherever it runs.
/// </summary>
internal sealed class LookRender : IDisposable
{
    private readonly GameEntity _entity;
    private readonly GameEntity _lightEntity;
    private readonly GameEntity[] _fills;
    public Vec3 Center { get; }
    public float Radius { get; }
    public string Note { get; } = "";

    private LookRender(GameEntity entity, GameEntity lightEntity, GameEntity[] fills, Vec3 center, float radius, string note)
    {
        _entity = entity; _lightEntity = lightEntity; _fills = fills; Center = center; Radius = radius; Note = note;
    }

    /// <summary>The piece at <paramref name="at"/>; null (with the reason) when the game gives no mesh for it.</summary>
    public static LookRender? Show(Scene scene, ItemObject item, Vec3 at, float lightIntensity, out string why)
    {
        why = "";
        MetaMesh? mesh;
        try { mesh = InventoryMesh(item); }
        catch (Exception ex) { why = "no inventory mesh: " + ex.GetBaseException().Message; return null; }
        if (mesh == null) { why = "the game gives no inventory mesh for " + item.StringId; return null; }

        var entity = GameEntity.CreateEmpty(scene, false, false, false);
        var frame = MatrixFrame.Identity;
        frame.origin = at;
        string note = "";
        var type = item.ItemType;
        bool female = item.ItemFlags.HasAnyFlag(ItemFlags.NotUsableByMale);
        if (type == ItemObject.ItemTypeEnum.HandArmor)
        {
            try { HandPose(entity, frame, mesh, female); }
            catch (Exception ex)
            {
                // Without the pose the gloves still show, arms out in the bind pose: wider, but every surface is there.
                note = "no hand pose (" + ex.GetBaseException().Message + "): bind pose";
                entity.SetGlobalFrame(frame);
                entity.AddMultiMesh(mesh, true);
            }
        }
        else
        {
            entity.SetGlobalFrame(frame);
            entity.AddMultiMesh(mesh, true);
            // The plain body part the tableau puts under the piece, so a hollow piece reads as worn.
            string baseMesh = type switch
            {
                ItemObject.ItemTypeEnum.HeadArmor or ItemObject.ItemTypeEnum.Cape => "base_head",
                ItemObject.ItemTypeEnum.BodyArmor when item.ArmorComponent?.MeshesMask.HasAnyFlag(SkinMask.BodyVisible) == true => "base_body",
                ItemObject.ItemTypeEnum.LegArmor when item.ArmorComponent?.MeshesMask.HasAnyFlag(SkinMask.LegsVisible) == true => "base_foot",
                ItemObject.ItemTypeEnum.HorseHarness => "horse_base_mesh",
                _ => "",
            };
            if (baseMesh.Length > 0)
            {
                if (female && type != ItemObject.ItemTypeEnum.HorseHarness) baseMesh += "_female";
                var body = MetaMesh.GetCopy(baseMesh, false, true);
                if (body != null) entity.AddMultiMesh(body, true);
            }
        }
        entity.RecomputeBoundingBox();
        var min = entity.GlobalBoxMin;
        var max = entity.GlobalBoxMax;
        var center = (min + max) * 0.5f;
        float radius = Math.Max(0.1f, (max - min).Length * 0.5f);
        // A posed glove's box holds the whole (invisible) skeleton; the hands sit in its middle, about a third of it.
        if (type == ItemObject.ItemTypeEnum.HandArmor && note.Length == 0) radius *= 0.38f;

        var lightEntity = PointLight(scene, center, radius * 12f, lightIntensity);
        // The fills: one each way round the piece, well out so none hot-spots a surface, at most of the camera
        // light's strength so the camera's side still reads as the front.
        var ways = new[] { new Vec3(1f, 0f, 0f), new Vec3(-1f, 0f, 0f), new Vec3(0f, 1f, 0f), new Vec3(0f, -1f, 0f), new Vec3(0f, 0f, 1f), new Vec3(0f, 0f, -1f) };
        var fills = ways.Select(w => PointLight(scene, center + w * (radius * 3f), radius * 8f, lightIntensity * 0.6f)).ToArray();
        return new LookRender(entity, lightEntity, fills, center, radius, note);
    }

    private static GameEntity PointLight(Scene scene, Vec3 at, float reach, float intensity)
    {
        var entity = GameEntity.CreateEmpty(scene, false, false, false);
        var light = Light.CreatePointLight(reach);
        light.LightColor = new Vec3(1f, 1f, 1f);
        light.Intensity = intensity;
        light.ShadowEnabled = false;
        entity.AddLight(light);
        var frame = MatrixFrame.Identity;
        frame.origin = at;
        entity.SetGlobalFrame(frame);
        return entity;
    }

    /// <summary>Where the camera goes to see the whole piece from <paramref name="angle"/> degrees round (0 = the side the
    /// piece faces), <paramref name="elevation"/> degrees above level; moves the light there too.</summary>
    public Vec3 Eye(float angle, float elevation, float fov)
    {
        // The bounding sphere is loose around a box's corners; 0.8 of it fills the frame without cutting the piece off.
        float distance = Radius / (float)Math.Sin(fov * 0.5f) * 0.8f;
        double a = angle * Math.PI / 180.0, e = elevation * Math.PI / 180.0;
        // A character faces +y in its own frame.
        var dir = new Vec3((float)(-Math.Sin(a) * Math.Cos(e)), (float)(Math.Cos(a) * Math.Cos(e)), (float)Math.Sin(e));
        var eye = Center + dir * distance;
        var light = MatrixFrame.Identity;
        light.origin = Center + dir * (distance * 0.8f) + new Vec3(0f, 0f, Radius * 0.6f);
        _lightEntity.SetGlobalFrame(light);
        return eye;
    }

    public void Dispose()
    {
        try { _entity.Remove(102); } catch (Exception) { }
        try { _lightEntity.Remove(102); } catch (Exception) { }
        foreach (var fill in _fills) { try { fill.Remove(102); } catch (Exception) { } }
    }

    /// <summary>ItemRosterElement.GetItemMeshForInventory (View assembly), by reflection: the mesh the designer shows.</summary>
    private static MetaMesh? InventoryMesh(ItemObject item)
    {
        // The game loads the View assembly from Modules\Native\bin, where Type.GetType's name lookup doesn't reach.
        var ext = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "TaleWorlds.MountAndBlade.View")
            .Select(a => a.GetType("TaleWorlds.MountAndBlade.View.ItemCollectionElementViewExtensions"))
            .FirstOrDefault(t => t != null);
        var get = ext?.GetMethod("GetItemMeshForInventory", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(ItemRosterElement), typeof(bool) }, null);
        if (get == null) throw new MissingMethodException("ItemCollectionElementViewExtensions.GetItemMeshForInventory(ItemRosterElement, bool)" + (ext == null ? " (no View assembly loaded)" : ""));
        return get.Invoke(null, new object[] { new ItemRosterElement(item, 1, null), false }) as MetaMesh;
    }

    /// <summary>
    /// The tableau's glove pose: a human skeleton in act_tableau_hand_armor_pose with the gloves and bare hands on it.
    /// Its own method so a game version without these calls throws here, where Show catches it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void HandPose(GameEntity entity, MatrixFrame frame, MetaMesh mesh, bool female)
    {
        var monster = Game.Current.DefaultMonster;
        var data = monster.FillAnimationSystemData(MBActionSet.GetActionSet(monster.ActionSetCode), 1f, false);
        entity.CreateSkeletonWithActionSet(ref data);
        entity.SetGlobalFrame(frame);
        // SetAgentActionChannel's ActionIndexCache parameter changed between game versions (by value, then `in`):
        // reflection takes whichever this game has.
        // It is an extension method (MBSkeletonExtensions.SetAgentActionChannel(this Skeleton, int, ActionIndexCache, ...)).
        var skeleton = entity.Skeleton;
        var set = typeof(MBSkeletonExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "SetAgentActionChannel" && m.GetParameters().Length >= 3 && m.GetParameters()[0].ParameterType == typeof(Skeleton))
            ?? throw new MissingMethodException("MBSkeletonExtensions.SetAgentActionChannel(Skeleton, int, ActionIndexCache, ...)");
        var ps = set.GetParameters();
        var cacheType = ps[2].ParameterType.IsByRef ? ps[2].ParameterType.GetElementType()! : ps[2].ParameterType;
        object action = cacheType.GetField("act_tableau_hand_armor_pose", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? cacheType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null)?.Invoke(null, new object[] { "act_tableau_hand_armor_pose" })
            ?? throw new MissingMemberException("ActionIndexCache.act_tableau_hand_armor_pose");
        var args = new object?[ps.Length];
        args[0] = skeleton; args[1] = 0; args[2] = action;
        for (int i = 3; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
        set.Invoke(null, args);
        entity.AddMultiMeshToSkeleton(mesh);
        var hand = MetaMesh.GetCopy(female ? "base_hand_female" : "base_hand", false, true);
        if (hand != null) entity.AddMultiMeshToSkeleton(hand);
        skeleton.TickActionChannels();
        skeleton.TickAnimationsAndForceUpdate(0.01f, frame, true);
    }
}
