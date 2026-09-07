using System.Text.Json;
using System.Text.Json.Nodes;
using MetroidvaniaStudio;
using MetroidvaniaStudio.Server;

internal static class PaletteGroupTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch(Exception e) when(e is ArgumentException or IOException or InvalidDataException or WorkspaceConflict) { return; } throw new Exception("Expected rejection."); }
    private static void Send(EditorWorkspace w, string action, params (string key, object? value)[] values)
    {
        var request = new Dictionary<string,object?> { ["action"]=action, ["clientId"]="palette-groups", ["commandId"]=Guid.NewGuid().ToString("N"), ["expectedRevision"]=w.Revision,["expectedInstanceId"]=w.InstanceId };
        foreach(var v in values)request[v.key]=v.value; w.Command(JsonSerializer.SerializeToElement(request));
    }
    public static void Ordering(EditorWorkspace w)
    {
        foreach(string name in new[]{"Green", "Blue", "Yellow"})Send(w,"paletteAdd",("name",name),("color","#345678"));
        string[] ids=w.Catalog.Data.GetProperty("materials").EnumerateArray().Select(m=>m.GetProperty("id").GetString()!).ToArray();
        var defaultGroup=w.Catalog.PaletteGroups.Single(); Check(defaultGroup.id=="default"&&defaultGroup.materials.SequenceEqual(ids),"Existing colors must belong to Default in their original order.");
        var materialData=w.Catalog.Data.GetProperty("materials").GetRawText();
        var files=Directory.EnumerateFiles(w.Files.TexturesPath,"*.png",SearchOption.AllDirectories).ToDictionary(p=>p,p=>File.ReadAllBytes(p));
        string map=MapDocumentStore.Serialize(w.Session.Document); long revision=w.DocumentRevision; bool undo=w.Session.CanUndo;
        Send(w,"paletteGroupAdd",("name","Caves"));string caves=w.Catalog.PaletteGroups.Last().id;
        Send(w,"paletteGroupAdd",("name","Tower"));string tower=w.Catalog.PaletteGroups.Last().id;
        Send(w,"paletteMove",("id",ids[1]),("groupId",caves),("beforeId",null));
        Send(w,"paletteMove",("id",ids[0]),("groupId",caves),("beforeId",ids[1]));
        Check(w.Catalog.PaletteGroups.Single(g=>g.id==caves).materials.SequenceEqual(new[]{ids[0],ids[1]}),"Moving across groups must respect the chosen insert position.");
        Send(w,"paletteMove",("id",ids[0]),("groupId",caves),("beforeId",null));
        Send(w,"paletteGroupMove",("id",caves),("beforeId","default"),("name","Crystal caves"));
        Check(w.Catalog.PaletteGroups[0].id==caves&&w.Catalog.PaletteGroups[0].name=="Crystal caves","Group names and order must persist.");
        Send(w,"paletteGroupMove",("id","default"),("beforeId",null));
        Check(w.Catalog.PaletteGroups.Select(g=>g.id).SequenceEqual(new[]{caves,tower,"default"}),"The default group can be reordered too.");
        Check(w.Catalog.PaletteGroups.Single(g=>g.id==tower).materials.Length==0,"Empty groups must be retained.");
        Check(materialData==w.Catalog.Data.GetProperty("materials").GetRawText(),"Grouping must not rewrite any material or resource path.");
        Check(map==MapDocumentStore.Serialize(w.Session.Document)&&revision==w.DocumentRevision&&undo==w.Session.CanUndo,"Organization must preserve map data and undo history.");
        Check(files.Count==Directory.EnumerateFiles(w.Files.TexturesPath,"*.png",SearchOption.AllDirectories).Count()&&files.All(p=>File.ReadAllBytes(p.Key).SequenceEqual(p.Value)),"Ordering cannot regenerate textures.");
        var reopened=new Catalog(w.Files);reopened.Refresh(); Check(JsonSerializer.Serialize(reopened.PaletteGroups)==JsonSerializer.Serialize(w.Catalog.PaletteGroups),"Groups must survive reopening.");
        var compact=JsonSerializer.SerializeToElement(w.State(false,false)); Check(!compact.TryGetProperty("paletteGroups",out _),"Brush replies must omit unchanged group metadata.");
        Send(w,"paletteAdd",("name","Purple"),("color","#9655CF"),("groupId",tower));
        Check(w.Catalog.PaletteGroups.Single(g=>g.id==tower).materials.Single()==w.Canvas.Material,"New palettes must join the chosen group.");
        var moved=w.Catalog.Material(ids[0]); var placement=new PalettePlacement(tower,null,w.Catalog.PaletteGroups);
        w.Catalog.ConfigurePalette(ids[0],"Renamed green","#245678",new PaletteTileset("template","",[]),null,moved,null,placement);
        Check(w.Catalog.PaletteGroups.Single(g=>g.id==tower).materials.Last()==ids[0]&&w.Catalog.Material(ids[0]).GetProperty("name").GetString()=="Renamed green","Palette edits and group changes must publish together.");
    }
    public static void Validation(EditorWorkspace w)
    {
        Send(w,"paletteAdd",("name","First"),("color","#112233")); string id=w.Canvas.Material;
        var initial=w.Catalog.PaletteGroups;
        Send(w,"paletteGroupAdd",("name","Area")); string group=w.Catalog.PaletteGroups.Last().id;
        string before=File.ReadAllText(w.Files.CatalogPath);
        Reject(()=>Send(w,"paletteGroupAdd",("name","area")));
        Reject(()=>Send(w,"paletteGroupAdd",("name","  ")));
        Reject(()=>Send(w,"paletteMove",("id",id),("groupId","missing"),("beforeId",null)));
        Reject(()=>Send(w,"paletteMove",("id",id),("groupId",group),("beforeId","missing")));
        Reject(()=>Send(w,"paletteGroupMove",("id",group),("beforeId","missing")));
        Reject(()=>Send(w,"paletteMove",("id",id),("groupId",group),("beforeId",null),("expectedGroups",initial)));
        Check(before==File.ReadAllText(w.Files.CatalogPath),"Invalid and stale organization requests must leave the catalog intact.");
        var material=w.Catalog.Material(id);var placement=new PalettePlacement(group,null,initial);
        Reject(()=>w.Catalog.ConfigurePalette(id,"Unexpected","#223344",new PaletteTileset("template","",[]),null,material,null,placement));
        Check(before==File.ReadAllText(w.Files.CatalogPath),"A stale group snapshot cannot partially save palette settings.");
        var node=JsonNode.Parse(before)!;node["opaqueValue"]=new JsonObject{{"keep","unchanged"}};node.AsObject().Remove("editorPaletteGroups");
        File.WriteAllText(w.Files.CatalogPath,node.ToJsonString());var legacy=new Catalog(w.Files);legacy.Refresh();
        Check(legacy.PaletteGroups.Single().id=="default"&&legacy.PaletteGroups.Single().materials.Single()==id,"A legacy catalog must show all palettes in Default without a migration write.");
        legacy.AddPaletteGroup("New area");Check(legacy.Data.GetProperty("opaqueValue").GetProperty("keep").GetString()=="unchanged","Opaque catalog fields must survive organization edits.");
        string valid=File.ReadAllText(w.Files.CatalogPath);File.WriteAllText(w.Files.CatalogPath,valid+" ");
        Reject(()=>legacy.AddPaletteGroup("Blocked"));Check(File.ReadAllText(w.Files.CatalogPath)==valid+" ","External catalog edits must be preserved.");
        var invalid=JsonNode.Parse(valid)!;invalid["editorPaletteGroups"]![1]!["materials"]=new JsonArray(id);
        File.WriteAllText(w.Files.CatalogPath,invalid.ToJsonString());Reject(()=>new Catalog(w.Files).Refresh());
    }
}
