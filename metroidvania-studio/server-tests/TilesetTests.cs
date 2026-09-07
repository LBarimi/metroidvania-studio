using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using MetroidvaniaStudio;
using MetroidvaniaStudio.Server;

internal static class TilesetTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is ArgumentException or IOException or InvalidDataException or WorkspaceConflict) { return; } throw new Exception("Expected rejected palette input."); }
    private static void Send(EditorWorkspace w, string action, params (string key, object? value)[] values)
    {
        var request = new Dictionary<string, object?> { ["action"] = action, ["clientId"] = "tilesets", ["commandId"] = Guid.NewGuid().ToString("N"),
            ["expectedRevision"] = w.Revision, ["expectedInstanceId"] = w.InstanceId };
        foreach (var v in values) request[v.key] = v.value;
        w.Command(JsonSerializer.SerializeToElement(request));
    }
    private static PaletteTileset Four() => new("four", "input", Enumerable.Range(0,8).Select(i => i < 4 ? new TilesetSlot(i * 16,0) : null).ToArray());
    private static PngRaster Markers()
    {
        var image = new PngRaster(64,16,new byte[64*16*4]);
        for (int y=0;y<16;y++) for(int x=0;x<64;x++) { int at=(y*64+x)*4; image.Pixels[at]=(byte)(x/16*50+20); image.Pixels[at+1]=(byte)(x%16*15); image.Pixels[at+2]=(byte)(y*15); image.Pixels[at+3]=255; }
        return image;
    }
    private static byte[] Pixel(PngRaster image,int x,int y) => image.Pixels.AsSpan((y*image.Width+x)*4,4).ToArray();
    private static bool Changed(string path,byte[] old) { try { return !File.ReadAllBytes(path).SequenceEqual(old); } catch(IOException) { return false; } }
    public static void FourRotations()
    {
        var source=Markers(); var output=TilesetComposer.Compose("atlas","#203040",Four(),source); var image=PngRaster.Decode(output.Png);
        // Explicit orientation cases: each must retain every pixel of the complete 16x16 sprite.
        (int mask,int slot,int turn)[] cases=[(124,0,0),(31,0,3),(199,0,2),(241,0,1),
            (112,1,0),(28,1,3),(7,1,2),(193,1,1),(127,2,0),(223,2,3),(247,2,2),(253,2,1),(255,3,0)];
        foreach(var c in cases)
        {
            var sprite=output.Sprites.Single(s=>s.shape==0&&s.mask==c.mask);
            for(int y=0;y<16;y++) for(int x=0;x<16;x++)
            {
                (int sx,int sy)=c.turn switch {1=>(y,15-x),2=>(15-x,15-y),3=>(15-y,x),_=>(x,y)};
                Check(Pixel(image,sprite.x+x,image.Height-sprite.y-16+y).SequenceEqual(Pixel(source,c.slot*16+sx,sy)), $"Whole sprite rotation changed mask {c.mask} at {x},{y}.");
            }
        }
        var isolated=output.Sprites.Single(s=>s.shape==0&&s.mask==0);
        foreach(var p in new[]{(0,0),(15,0),(0,15),(15,15)}) Check(Pixel(image,isolated.x+p.Item1,image.Height-isolated.y-16+p.Item2)[0]==70,"Isolated tile needs four convex corners.");
        Check(output.Sprites.Count==51 && output.Sprites.Where(s=>s.shape==0).Select(s=>s.mask).Distinct().Count()==47,"Every normalized mask needs a sprite.");
    }
    public static void FullAtlas()
    {
        var original=PaletteAtlas.Create("atlas","#254768");
        var ordered=PngRaster.Decode(TilesetComposer.Template("blob47","#254768"));
        var settings=new PaletteTileset("blob47","input",Enumerable.Range(0,51).Select(i=>(TilesetSlot?)new TilesetSlot(i%8*16,i/8*16)).ToArray());
        var composed=TilesetComposer.Compose("atlas","#254768",settings,ordered);
        Check(PngRaster.Decode(original.Png).Pixels.SequenceEqual(PngRaster.Decode(composed.Png).Pixels),"47-slot template must round-trip all masks and slopes without vertical flips.");
        settings.slots[0]=null; settings.slots[50]=null;
        Check(PngRaster.Decode(TilesetComposer.Compose("atlas","#254768",settings,ordered).Png).Pixels.SequenceEqual(PngRaster.Decode(original.Png).Pixels),"Empty slots must retain template pixels, including slopes.");
        var four=Four(); four.slots[0]=null; Reject(()=>TilesetComposer.Compose("atlas","#254768",four,Markers()));
        settings.slots[0]=new TilesetSlot(120,0); Reject(()=>TilesetComposer.Compose("atlas","#254768",settings,ordered));
    }
    public static void Storage(EditorWorkspace w)
    {
        Send(w,"paletteAdd",("name","Tile test"),("color","#203040")); string id=w.Canvas.Material;
        string map=MapDocumentStore.Serialize(w.Session.Document); long revision=w.DocumentRevision;
        var input=Markers(); var before=w.Catalog.Material(id);
        Send(w,"paletteConfigure",("id",id),("name","Tile test"),("color","#203040"),("settings",Four()),("uploads",new Dictionary<string,string>{{"input",Convert.ToBase64String(input.Encode())}}),("expectedMaterial",before));
        var atlasMaterial=w.Catalog.Material(id); byte[] atlas=File.ReadAllBytes(w.Files.Asset(atlasMaterial.GetProperty("sprites")[0].GetProperty("asset").GetString()!));
        var settings=Four(); var uploads=new Dictionary<string,string>();
        for(int i=0;i<4;i++)
        {
            string key="part"+i; var part=new PngRaster(16,16,new byte[1024]);
            for(int y=0;y<16;y++) for(int x=0;x<16;x++) part.CopyPixel(input,i*16+x,y,x,y);
            uploads[key]=Convert.ToBase64String(part.Encode()); settings.slots[i]=new TilesetSlot(0,0,key);
        }
        settings=settings with{source="part0"};
        Send(w,"paletteConfigure",("id",id),("name","Tile test"),("color","#203040"),("settings",settings),("uploads",uploads),("expectedMaterial",atlasMaterial));
        var next=w.Catalog.Material(id); byte[] separate=File.ReadAllBytes(w.Files.Asset(next.GetProperty("sprites")[0].GetProperty("asset").GetString()!));
        Check(PngRaster.Decode(atlas).Pixels.SequenceEqual(PngRaster.Decode(separate).Pixels),"Four separate images must produce the same atlas as one strip.");
        var saved=JsonSerializer.Deserialize<PaletteTileset>(next.GetProperty("editorTileset"),Catalog.Json)!;
        Check(saved.slots.Take(4).Select(s=>s!.asset).Distinct().Count()==4 && saved.slots.Take(4).All(s=>File.Exists(w.Files.Asset(s!.asset!))),"Each original PNG must be saved as a portable resource.");
        Check(!saved.source.Contains("upload-") && !saved.source.Contains(":"),"Temporary upload keys must not enter the catalog.");
        var reopened=new Catalog(w.Files); reopened.Refresh(); Check(reopened.Material(id).GetRawText()==next.GetRawText(),"Settings must survive reload.");
        Check(map==MapDocumentStore.Serialize(w.Session.Document)&&revision==w.DocumentRevision,"Palette changes cannot rewrite map cells or history.");
        Reject(()=>w.Catalog.ConfigurePalette(id,"Tile test","#203040",Four(),null,before));
        string catalog=File.ReadAllText(w.Files.CatalogPath);
        Reject(()=>w.Catalog.ConfigurePalette(id,"Tile test","#203040",Four(),"not-png",next));
        Check(File.ReadAllText(w.Files.CatalogPath)==catalog,"Rejected PNGs must preserve the catalog.");
        var foreign=Four() with{source="Textures/not-registered.png"};
        Reject(()=>w.Catalog.ConfigurePalette(id,"Tile test","#203040",foreign,null,next));
        Check(File.ReadAllText(w.Files.CatalogPath)==catalog,"Unregistered source references must be rejected.");
    }
    public static void HotReload(EditorWorkspace w)
    {
        Storage(w); string id=w.Canvas.Material; var material=w.Catalog.Material(id);
        var settings=JsonSerializer.Deserialize<PaletteTileset>(material.GetProperty("editorTileset"),Catalog.Json)!;
        string source=w.Files.Asset(settings.slots[3]!.asset!), atlas=w.Files.Asset(material.GetProperty("sprites")[0].GetProperty("asset").GetString()!);
        string map=MapDocumentStore.Serialize(w.Session.Document); long docRevision=w.DocumentRevision;
        var until=System.Diagnostics.Stopwatch.StartNew();
        while(until.ElapsedMilliseconds<700){w.Tick();Thread.Sleep(10);}
        byte[] old=File.ReadAllBytes(atlas); var image=PngRaster.Decode(File.ReadAllBytes(source)); image.Pixels[0]=251;
        File.WriteAllBytes(source,image.Encode());
        Check(SpinWait.SpinUntil(()=>{w.Tick();return Changed(atlas,old);},7000),"Changing a non-primary source must rebuild the generated atlas.");
        byte[] updated=File.ReadAllBytes(atlas);
        using(var broken=new MemoryStream())
        {
            broken.Write(image.Encode().AsSpan(0,33)); PaletteAtlas.Chunk(broken,"IDAT",[1,2,3]); PaletteAtlas.Chunk(broken,"IEND",[]);
            Check(Catalog.CompleteImage(broken.ToArray(),".png"),"The corrupt fixture must pass the chunk CRC probe and fail during decoding.");
            File.WriteAllBytes(source,broken.ToArray());
        }
        until.Restart(); while(until.ElapsedMilliseconds<1200){w.Tick();Thread.Sleep(10);}
        Check(File.ReadAllBytes(atlas).SequenceEqual(updated),"A partial source save must retain the last complete atlas.");
        image.Pixels[0]=252; File.WriteAllBytes(source,image.Encode());
        Check(SpinWait.SpinUntil(()=>{w.Tick();return Changed(atlas,updated);},7000),"A complete replacement must recover after a partial save.");
        var reopened=new Catalog(w.Files); reopened.Refresh(); old=File.ReadAllBytes(atlas); image.Pixels[0]=253; File.WriteAllBytes(source,image.Encode());
        Check(SpinWait.SpinUntil(()=>{reopened.RefreshTextures();return Changed(atlas,old);},7000),"A restarted catalog must rebuild from changed originals.");
        Check(w.DocumentRevision==docRevision&&map==MapDocumentStore.Serialize(w.Session.Document),"Texture workers cannot invalidate brush gestures or map history.");
    }
    public static void PngFormats()
    {
        // Distinct coordinates expose row/filter, channel, sub-byte and pass-order mistakes.
        foreach(var mode in new[]{(6,8),(6,16),(2,8),(0,4),(3,4),(4,8)}) foreach(int interlace in new[]{0,1}) foreach(int filter in Enumerable.Range(0,5))
        {
            var (png,expected)=EncodedFixture(mode.Item1,mode.Item2,interlace,filter);
            Check(PngRaster.Decode(png).Pixels.SequenceEqual(expected),$"PNG type {mode} interlace {interlace} filter {filter} differs.");
            png[^1]^=1; Reject(()=>PngRaster.Decode(png));
        }
        var valid=Markers().Encode(); Reject(()=>PngRaster.Decode(valid[..^8]));
    }
    private static (byte[] png,byte[] expected) EncodedFixture(int type,int depth,int interlace,int filter)
    {
        const int width=17,height=19; int channels=type switch{6=>4,2=>3,4=>2,_=>1};
        byte[] expected=new byte[width*height*4];
        int Value(int x,int y,int c)=>depth==4?(x*3+y*5)%16: (x*17+y*31+c*43)%256;
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            int offset=(y*width+x)*4, v=Value(x,y,0); expected[offset]=(byte)(depth==4?v*17:v);
            expected[offset+1]=(byte)(type is 0 or 3 or 4?expected[offset]:Value(x,y,1));
            expected[offset+2]=(byte)(type is 0 or 3 or 4?expected[offset]:Value(x,y,2));
            expected[offset+3]=(byte)(type==3?v*17:type==6?Value(x,y,3):type==4?Value(x,y,1):255);
        }
        using var raw=new MemoryStream();
        (int x,int y,int dx,int dy)[] passes=interlace==0?[(0,0,1,1)]:[(0,0,8,8),(4,0,8,8),(0,4,4,8),(2,0,4,4),(0,2,2,4),(1,0,2,2),(0,1,1,2)];
        foreach(var p in passes)
        {
            int cols=(width-p.x+p.dx-1)/p.dx,rows=(height-p.y+p.dy-1)/p.dy,stride=(cols*channels*depth+7)/8,bpp=Math.Max(1,channels*depth/8);
            byte[] prior=new byte[stride];
            for(int y=0;y<rows;y++)
            {
                byte[] row=new byte[stride];
                for(int x=0;x<cols;x++)for(int c=0;c<channels;c++)
                {
                    int v=Value(p.x+x*p.dx,p.y+y*p.dy,c),index=x*channels+c;
                    if(depth==4)row[index/2]|=(byte)(v<<(index%2==0?4:0));
                    else if(depth==16){row[index*2]=(byte)v;row[index*2+1]=(byte)v;}
                    else row[index]=(byte)v;
                }
                raw.WriteByte((byte)filter);
                for(int i=0;i<stride;i++)
                {
                    int left=i>=bpp?row[i-bpp]:0,up=prior[i],corner=i>=bpp?prior[i-bpp]:0;
                    int predictor=left+up-corner;
                    int[] candidates=[left,up,corner]; int paeth=candidates.OrderBy(v=>Math.Abs(predictor-v)).First();
                    int delta=filter switch{0=>0,1=>left,2=>up,3=>(left+up)/2,_=>paeth};
                    raw.WriteByte(unchecked((byte)(row[i]-delta)));
                }
                prior=row;
            }
        }
        using var png=new MemoryStream(); png.Write(new byte[]{137,80,78,71,13,10,26,10}); byte[] header=new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header,width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),height); header[8]=(byte)depth;header[9]=(byte)type;header[12]=(byte)interlace;
        PaletteAtlas.Chunk(png,"IHDR",header);
        if(type==3){ PaletteAtlas.Chunk(png,"PLTE",Enumerable.Range(0,16).SelectMany(i=>new byte[]{(byte)(i*17),(byte)(i*17),(byte)(i*17)}).ToArray()); PaletteAtlas.Chunk(png,"tRNS",Enumerable.Range(0,16).Select(i=>(byte)(i*17)).ToArray()); }
        using var compressed=new MemoryStream(); using(var zlib=new ZLibStream(compressed,CompressionLevel.Fastest,true))zlib.Write(raw.ToArray());
        PaletteAtlas.Chunk(png,"IDAT",compressed.ToArray()); PaletteAtlas.Chunk(png,"IEND",[]); return(png.ToArray(),expected);
    }
}
