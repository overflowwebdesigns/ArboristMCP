Arborist Bridge (v0.1.0)
=======================

What it is
- ArboristBridge.dll : BepInEx plugin loaded inside Arborist. Listens on http://127.0.0.1:47811/ (this PC only).
- ArboristMcp.exe    : MCP server for Claude. Passes tool calls to the plugin. Uses the .NET Framework built into Windows.

Claude desktop config entry (Settings > Developer > Edit Config, inside "mcpServers"):
    "arborist": {
      "command": "C:\\Tools\\Data\\Application\\BepInEx\\plugins\\ArboristBridge\\ArboristMcp.exe"
    }

Tools: arborist_status, arborist_list_catalog, arborist_list_planted, arborist_ground_height,
       arborist_plant, arborist_remove, arborist_move, arborist_undo,
       arborist_get_camera, arborist_set_camera, arborist_screenshot, arborist_save

Notes
- Changes show up live but are only written to the course when you save (in Arborist, or with arborist_save).
- Items planted by the bridge use source ID 5, the same as Arborist's paint brush, so the brush can erase them.
- Log file: C:\Tools\Data\Application\BepInEx\LogOutput.log
- Change the port in BepInEx\config\com.sableoaks.arboristbridge.cfg (set ARBORIST_BRIDGE_PORT for the exe to match).

To remove everything
Close Arborist, then delete these from C:\Tools\Data\Application:
  winhttp.dll, doorstop_config.ini, .doorstop_version, and the BepInEx folder.
Then remove the "arborist" entry from the Claude config.
