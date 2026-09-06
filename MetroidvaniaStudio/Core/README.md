# Editing core

This library owns the versioned JSON document model, editing transactions, room layout, brush geometry, selection, minimap geometry and resource catalog definitions. It uses only the .NET base class library.

The model uses tile units with positive Y pointing upward. Tile textures are 16 by 16 pixels. Resource identifiers are opaque strings resolved through the workspace catalog. No rendering lifecycle or game object implementation is part of the editing core.

`MapJson` writes public data fields with the standard JSON attribute rules; calculated properties and transient state are excluded. Version 2 map files retain their field names, numeric enum values and coordinates. Unknown custom object and styleground data survives round trips.

Run the regression suite with `dotnet run --project MetroidvaniaStudio/Core.Tests/MetroidvaniaStudio.Core.Tests.csproj` from the repository root.
