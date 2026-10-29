// Shared MCP tool definitions (compiled into both the plugin and the connector exe).
using System;
using System.Collections.Generic;

namespace ArboristBridge
{
    public static class ToolDefs
    {
        static Dictionary<string, object> D(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }
        static List<object> L(params object[] xs) { return new List<object>(xs); }
        static Dictionary<string, object> P(string type, string desc) { return D("type", type, "description", desc); }
        static Dictionary<string, object> Tool(string name, string desc, Dictionary<string, object> props, params string[] required)
        {
            var schema = D("type", "object", "properties", props);
            if (required.Length > 0) schema["required"] = new List<object>(required);
            return D("name", name, "description", desc, "inputSchema", schema);
        }

        public static List<object> Tools()
        {
            var areaProps = new Func<Dictionary<string, object>>(() => D(
                "x", P("number", "Center X (meters, world space)"),
                "z", P("number", "Center Z (meters, world space)"),
                "radius", P("number", "Radius around x/z in meters"),
                "min_x", P("number", "Rectangle min X (alternative to x/z/radius)"),
                "min_z", P("number", "Rectangle min Z"),
                "max_x", P("number", "Rectangle max X"),
                "max_z", P("number", "Rectangle max Z"),
                "item_id", P("string", "Only this vegetation item id"),
                "item_ids", D("type", "array", "items", D("type", "string"), "description", "Only these vegetation item ids")));

            var listProps = areaProps();
            listProps["limit"] = P("integer", "Max individual items returned (default 200)");
            listProps["summary_only"] = P("boolean", "Only return counts per item type");

            var removeProps = areaProps();
            removeProps["max_count"] = P("integer", "Remove at most this many (nearest first when using x/z)");
            removeProps["dry_run"] = P("boolean", "Report what would be removed without removing");

            var plantItem = D("type", "object", "properties", D(
                "item_id", P("string", "Vegetation item id from arborist_list_catalog"),
                "x", P("number", "World X in meters"),
                "z", P("number", "World Z in meters"),
                "y", P("number", "Explicit height; only used when snap=false"),
                "snap", P("boolean", "Drop onto the terrain mesh (default true unless y is given)"),
                "y_offset", P("number", "Extra vertical offset in meters (negative sinks it)"),
                "scale", P("number", "Uniform scale; default random within the item's min/max scale"),
                "scale_xyz", D("type", "array", "items", D("type", "number"), "description", "Explicit [x,y,z] scale"),
                "rotation_y", P("number", "Yaw in degrees; default random")),
                "required", L("item_id", "x", "z"));

            return L(
                Tool("arborist_status", "Check the bridge connection and what's loaded in Arborist: course path, vegetation area bounds, planted total, camera position.", D()),
                Tool("arborist_list_catalog", "List vegetation items (trees, plants, grass, objects) available in the loaded course's biomes, with ids and current planted counts.",
                     D("filter", P("string", "Case-insensitive text to match in item or biome name"),
                       "type", P("string", "Filter by type: Tree, Plant, Grass, Objects, LargeObjects"))),
                Tool("arborist_list_planted", "Find planted vegetation in an area (circle x/z/radius or rectangle) or across the whole course, optionally filtered by item id.", listProps),
                Tool("arborist_ground_height", "Sample terrain height, surface mesh name and slope at one or more x/z points.",
                     D("x", P("number", "X for a single point"), "z", P("number", "Z for a single point"),
                       "points", D("type", "array", "items", D("type", "object", "properties", D("x", P("number", "X"), "z", P("number", "Z"))), "description", "Several points"))),
                Tool("arborist_plant", "Plant one or more vegetation items at x/z positions. Items snap to the terrain mesh by default and get random scale/rotation unless given.",
                     D("items", D("type", "array", "items", plantItem, "description", "Items to plant")), "items"),
                Tool("arborist_remove", "Remove planted vegetation in a circle or rectangle (optionally only certain item ids). Use dry_run first for large areas.", removeProps),
                Tool("arborist_move", "Move/rescale/rotate the planted item nearest to x/z (within radius).",
                     D("x", P("number", "X of the existing item"), "z", P("number", "Z of the existing item"),
                       "radius", P("number", "Search radius in meters (default 1)"),
                       "item_id", P("string", "Only match this item id"),
                       "new_x", P("number", "New X"), "new_z", P("number", "New Z"),
                       "y_offset", P("number", "Vertical offset after snapping"),
                       "scale", P("number", "New uniform scale"), "rotation_y", P("number", "New yaw in degrees")), "x", "z"),
                Tool("arborist_undo", "Undo the last bridge plant/remove/move command(s). Only covers changes made through this bridge since Arborist started.",
                     D("steps", P("integer", "How many commands to undo (default 1)"))),
                Tool("arborist_get_camera", "Get Arborist's camera position and rotation.", D()),
                Tool("arborist_set_camera", "Move Arborist's camera. Give x/z plus y or height_above_ground, and either look_at_x/look_at_z or yaw/pitch.",
                     D("x", P("number", "X"), "z", P("number", "Z"), "y", P("number", "Absolute height"),
                       "height_above_ground", P("number", "Height above terrain at x/z"),
                       "look_at_x", P("number", "Point to look at X"), "look_at_z", P("number", "Point to look at Z"),
                       "look_at_y", P("number", "Point to look at Y (default ground + 1.5 m)"),
                       "yaw", P("number", "Yaw degrees"), "pitch", P("number", "Pitch degrees (positive looks down)"))),
                Tool("arborist_screenshot", "Capture what Arborist is currently showing (its real rendered frame), with the UI hidden by default.",
                     D("width", P("integer", "Output width in pixels (default 1280, max = Arborist window width); height follows the window's aspect"),
                       "hide_ui", P("boolean", "Hide Arborist's menus for the capture (default true)"))),
                Tool("arborist_save", "Save the course's vegetation in Arborist (same as Arborist's own Save).", D()),
                Tool("arborist_list_biomes", "List the course's biomes (vegetation packages) with every item's placement rules: density, sample distance, noise, height/steepness, biome-edge, concave and texture-mask rules.",
                     D("filter", P("string", "Only biomes whose name contains this text"),
                       "include_rules", P("boolean", "Include full per-item rule fields (default true)"))),
                Tool("arborist_list_masks", "List biome masks (polygons that paint an area with a biome), vegetation masks and vegetation lines: name, biome type, blend distance, noise, size, and optionally node outlines. Filter by an area to see what covers a spot.",
                     D("kind", P("string", "all | biome | vegetation | line (default all)"),
                       "filter", P("string", "Text to match in mask name or biome type"),
                       "x", P("number", "Only masks near/containing this X"), "z", P("number", "...and Z"),
                       "radius", P("number", "Search radius in meters (default 25)"),
                       "include_nodes", P("boolean", "Include the outline node positions"),
                       "limit", P("integer", "Max masks returned (default 500)"))),
                Tool("arborist_find_objects", "Find mesh objects in the Arborist scene (course meshes like water, creek, cart path) by name and/or near a point.",
                     D("filter", P("string", "Text to match in object name"), "x", P("number", "Near X"), "z", P("number", "Near Z"),
                       "radius", P("number", "Search radius in meters (default 25)"), "limit", P("integer", "Max results (default 50)"))),
                Tool("arborist_mesh_outline", "Trace the boundary outline(s) of a mesh object, the same way Arborist's 'mask from mesh' does. Loops are sorted largest first.",
                     D("object", P("string", "Object name or full path"), "simplify", P("boolean", "Thin points like Arborist (default true)"),
                       "include_points", P("boolean", "Return the outline points (default true)")), "object"),
                Tool("arborist_create_vegetation_mask", "Create an Arborist vegetation mask (removes vegetation types inside) from a mesh outline or points, optionally grown/shrunk by meters. Lives in Arborist's VegetationMasks list and is saved with the course only when the user saves.",
                     D("name", P("string", "Mask name"), "from_object", P("string", "Mesh object to trace"), "loop_index", P("integer", "Which outline loop (default 0 = largest)"),
                       "points", D("type", "array", "items", D("type", "array", "items", D("type", "number")), "description", "Outline as [[x,z],...] or [[x,y,z],...]"),
                       "inflate", P("number", "Grow (+) or shrink (-) the outline by meters"),
                       "remove_grass", P("boolean", "default true"), "remove_plants", P("boolean", "default true"), "remove_trees", P("boolean", "default false"),
                       "remove_objects", P("boolean", "default false"), "remove_large_objects", P("boolean", "default false"), "include_nodes", P("boolean", "Return nodes"))),
                Tool("arborist_create_biome_mask", "Create an Arborist biome mask (paints an area with a biome) from a mesh outline or points, optionally grown/shrunk by meters. Saved with the course only when the user saves.",
                     D("name", P("string", "Mask name"), "biome", P("string", "Biome name (e.g. 'lake edges') or type (e.g. OPCD_Biome5)"),
                       "from_object", P("string", "Mesh object to trace"), "loop_index", P("integer", "Which outline loop (default 0 = largest)"),
                       "points", D("type", "array", "items", D("type", "array", "items", D("type", "number")), "description", "Outline as [[x,z],...] or [[x,y,z],...]"),
                       "inflate", P("number", "Grow (+) or shrink (-) the outline by meters"),
                       "blend_distance", P("number", "default 0"), "use_noise", P("boolean", "default true"), "noise_scale", P("number", "default 20"),
                       "tight_edges", P("boolean", "default false"), "copy_curves_from", P("string", "Copy blend curves from an existing biome mask by name"),
                       "include_nodes", P("boolean", "Return nodes")), "biome"),
                Tool("arborist_export_mesh", "Export a mesh object's world-space vertices and triangles to a JSON file in Arborist's BepInEx\\exports folder (for outline/geometry work outside Arborist).",
                     D("object", P("string", "Object name or full path")), "object"),
                Tool("arborist_call", "Run any Arborist bridge command by name (use when a command exists in the plugin but isn't in this tool list yet). cmd 'list_commands' lists them; args are that command's arguments.",
                     D("cmd", P("string", "Command name without the arborist_ prefix, e.g. pp_get, pp_set, export_mesh"),
                       "args", D("type", "object", "description", "Arguments for that command")), "cmd"),
                Tool("arborist_pp_get", "Read Arborist's post-processing settings (Unity post-processing profile): each effect's on/off state and every parameter Arborist saves, with value, override flag, range and enum options. Effects: AmbientOcclusion, AutoExposure, Bloom, ChromaticAberration, ColorGrading, DepthOfField, Grain, LensDistortion, MotionBlur, ScreenSpaceReflections, Vignette.",
                     D("effect", P("string", "Only this effect (e.g. ColorGrading)"))),
                Tool("arborist_pp_set", "Change post-processing live in Arborist. Only parameters Arborist saves to the course are allowed. Sets the parameter's override flag on by default. Arborist will prompt the user to save; the bridge never saves post processing itself. Colors as [r,g,b,a] or '#RRGGBB'; ColorGrading lift/gamma/gain as [r,g,b,w] (w = overall offset); enums by name.",
                     D("effect", P("string", "Effect name, e.g. ColorGrading"),
                       "active", P("boolean", "Turn the effect on/off"),
                       "params", D("type", "object", "description", "Parameter -> value, e.g. {\"saturation\": 10, \"temperature\": -5}"),
                       "set_override", P("boolean", "Also tick the parameter's override box (default true)"),
                       "changes", D("type", "array", "items", D("type", "object"), "description", "Several effects at once: [{effect, active?, params?, set_override?}]"))),
                Tool("arborist_pp_undo", "Undo the last bridge post-processing change(s).", D("steps", P("integer", "How many (default 1)"))),
                Tool("arborist_pp_revert_to_saved", "Reload post processing from the course's saved arborist_postprocess.dat, discarding unsaved live changes (can be undone with pp_undo).", D()),
                Tool("arborist_delete_masks", "Delete masks that this bridge created during the current Arborist session (never touches the user's own masks).",
                     D("names", D("type", "array", "items", D("type", "string"), "description", "Mask names to delete"),
                       "all_created", P("boolean", "Delete every bridge-created mask")))
            );
        }

    }
}
