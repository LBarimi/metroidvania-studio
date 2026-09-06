#pragma once
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <limits>
#include <map>
#include <set>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

// Portable import data. Coordinates are source pixels, X right and Y up.
namespace MetroidvaniaStudio {
inline void Require(bool ok, const std::string& message) { if (!ok) throw std::runtime_error(message); }
struct Json {
    enum class Kind { Null, Boolean, Number, String, Array, Object } kind = Kind::Null;
    bool boolean = false;
    double number = 0;
    std::string string;
    std::vector<Json> array;
    std::map<std::string, Json> object;
    const Json& Get(const std::string& key) const {
        static const Json empty;
        const auto it = object.find(key); return it == object.end() ? empty : it->second;
    }
    std::string Text(const std::string& fallback = "") const {
        if (kind == Kind::Null) return fallback;
        Require(kind == Kind::String, "Expected a JSON string."); return string;
    }
    double Number(double fallback = 0) const {
        if (kind == Kind::Null) return fallback;
        Require(kind == Kind::Number, "Expected a JSON number."); return number;
    }
    int Integer(int fallback = 0) const {
        const double n = Number(fallback);
        Require(n >= INT32_MIN && n <= INT32_MAX && std::floor(n) == n, "Expected a bounded integer."); return static_cast<int>(n);
    }
    bool Bool(bool fallback = false) const {
        if (kind == Kind::Null) return fallback;
        Require(kind == Kind::Boolean, "Expected a JSON boolean."); return boolean;
    }
    const std::vector<Json>& Items() const {
        Require(kind == Kind::Array || kind == Kind::Null, "Expected a JSON array."); return array;
    }
};

class JsonReader {
    const std::string& text; size_t p = 0;
    void Space() { while (p < text.size() && (text[p]==' ' || text[p]=='\t' || text[p]=='\r' || text[p]=='\n')) ++p; }
    char Take() { Require(p < text.size(), "Unexpected end of JSON."); return text[p++]; }
    bool Eat(char c) { Space(); if (p < text.size() && text[p] == c) { ++p; return true; } return false; }
    unsigned Hex4() {
        unsigned n = 0;
        for (int i = 0; i < 4; ++i) { const char c=Take(); int v=c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:c>='A'&&c<='F'?c-'A'+10:-1; Require(v>=0,"Invalid Unicode escape."); n=n*16+v; }
        return n;
    }
    static void Utf8(std::string& out, unsigned c) {
        if(c<0x80) out+=static_cast<char>(c);
        else if(c<0x800) { out+=static_cast<char>(0xc0|(c>>6)); out+=static_cast<char>(0x80|(c&63)); }
        else if(c<0x10000) { out+=static_cast<char>(0xe0|(c>>12)); out+=static_cast<char>(0x80|((c>>6)&63)); out+=static_cast<char>(0x80|(c&63)); }
        else { out+=static_cast<char>(0xf0|(c>>18)); out+=static_cast<char>(0x80|((c>>12)&63)); out+=static_cast<char>(0x80|((c>>6)&63)); out+=static_cast<char>(0x80|(c&63)); }
    }
    std::string String() {
        Require(Take()=='"',"Expected a quoted string."); std::string out;
        while (true) {
            const unsigned char c=static_cast<unsigned char>(Take()); if(c=='"') return out;
            Require(c>=32,"Control character in JSON string.");
            if(c!='\\') { out+=static_cast<char>(c); continue; }
            switch(Take()) {
            case '"':out+='"';break; case '\\':out+='\\';break; case '/':out+='/';break;
            case 'b':out+='\b';break;case 'f':out+='\f';break;case 'n':out+='\n';break;case 'r':out+='\r';break;case 't':out+='\t';break;
            case 'u': { unsigned n=Hex4(); if(n>=0xd800&&n<=0xdbff) { Require(Take()=='\\'&&Take()=='u',"Missing Unicode surrogate."); const unsigned low=Hex4(); Require(low>=0xdc00&&low<=0xdfff,"Invalid Unicode surrogate."); n=0x10000+((n-0xd800)<<10)+(low-0xdc00); } else Require(n<0xdc00||n>0xdfff,"Unexpected Unicode surrogate."); Utf8(out,n); break; }
            default: throw std::runtime_error("Invalid JSON escape.");
            }
        }
    }
    Json Value(int depth) {
        Require(depth<64,"JSON nesting limit exceeded."); Space(); Require(p<text.size(),"Missing JSON value."); Json j;
        if(text[p]=='{') { ++p;j.kind=Json::Kind::Object;if(Eat('}'))return j; do { Space();auto key=String();Require(Eat(':'),"Missing JSON colon.");Require(j.object.emplace(key,Value(depth+1)).second,"Duplicate JSON key."); } while(Eat(','));Require(Eat('}'),"Missing JSON closing brace.");return j; }
        if(text[p]=='[') { ++p;j.kind=Json::Kind::Array;if(Eat(']'))return j;do { j.array.push_back(Value(depth+1)); } while(Eat(','));Require(Eat(']'),"Missing JSON closing bracket.");return j; }
        if(text[p]=='"') { j.kind=Json::Kind::String;j.string=String();return j; }
        for(const auto& word: {std::string("true"),std::string("false"),std::string("null")}) if(text.compare(p,word.size(),word)==0) { p+=word.size();j.kind=word=="null"?Json::Kind::Null:Json::Kind::Boolean;j.boolean=word=="true";return j; }
        size_t start=p; if(text[p]=='-')++p;
        auto digit=[&] { return p<text.size()&&text[p]>='0'&&text[p]<='9'; };
        Require(digit(),"Invalid JSON value."); if(text[p]=='0')++p;else while(digit())++p;
        if(p<text.size()&&text[p]=='.') { ++p;Require(digit(),"Missing decimal digits.");while(digit())++p; }
        if(p<text.size()&&(text[p]=='e'||text[p]=='E')) { ++p;if(p<text.size()&&(text[p]=='+'||text[p]=='-'))++p;Require(digit(),"Missing exponent digits.");while(digit())++p; }
        std::istringstream stream(text.substr(start,p-start));stream.imbue(std::locale::classic());stream>>j.number;
        Require(!stream.fail()&&std::isfinite(j.number),"JSON number is outside range.");j.kind=Json::Kind::Number;return j;
    }
public:
    explicit JsonReader(const std::string& input):text(input) {}
    Json Read() {
        Require(text.size()<=32*1024*1024,"JSON exceeds 32 MiB.");
        if(text.compare(0,3,"\xef\xbb\xbf")==0)p=3;
        // Reject malformed UTF-8 before interpreting keys or resource paths.
        for(size_t i=p;i<text.size();) {
            unsigned c=static_cast<unsigned char>(text[i++]);if(c<128)continue;
            int n=c>=0xc2&&c<=0xdf?1:c>=0xe0&&c<=0xef?2:c>=0xf0&&c<=0xf4?3:-1;
            Require(n>0&&i+n<=text.size(),"Invalid UTF-8.");unsigned value=c&((1u<<(6-n))-1u);
            for(int k=0;k<n;++k) { unsigned b=static_cast<unsigned char>(text[i++]);Require((b&0xc0)==0x80,"Invalid UTF-8.");value=(value<<6)|(b&63); }
            Require(value>=(n==1?0x80u:n==2?0x800u:0x10000u)&&value<=0x10ffff&&!(value>=0xd800&&value<=0xdfff),"Invalid UTF-8.");
        }
        auto j=Value(0);Space();Require(p==text.size(),"Trailing JSON data.");return j;
    }
};
inline std::string PathUtf8(const std::filesystem::path& path) { const auto bytes=path.u8string(); return std::string(reinterpret_cast<const char*>(bytes.data()),bytes.size()); }
inline std::string ReadText(const std::filesystem::path& path) {
    const auto size=std::filesystem::file_size(path);Require(size<=32*1024*1024,"JSON exceeds 32 MiB.");
    std::ifstream file(path,std::ios::binary);Require(file.good(),"Cannot open JSON.");std::string result(static_cast<size_t>(size),'\0');file.read(result.data(),static_cast<std::streamsize>(size));Require(file.good(),"Cannot read JSON.");return result;
}
inline std::filesystem::path ResourcePath(const std::filesystem::path& root,const std::string& asset) {
    Require(!asset.empty()&&asset.find(':')==std::string::npos&&asset.find('\\')==std::string::npos&&asset.find('\0')==std::string::npos,"Invalid resource path.");
    auto relative=std::filesystem::u8path(asset);Require(!relative.is_absolute(),"Resource path must be relative.");
    for(const auto& part:relative)Require(part!=".."&&part!=".","Resource path traversal is forbidden.");
    const auto base=std::filesystem::canonical(root),target=std::filesystem::canonical(base/relative);
    auto a=base.begin(),b=target.begin();for(;a!=base.end()&&b!=target.end()&&*a==*b;++a,++b){}
    Require(a==base.end()&&b!=target.end(),"Resource links must stay inside the resource directory.");return target;
}
struct Point { double x=0,y=0; };
struct Color { float r=1,g=1,b=1,a=1; };
inline Color ParseColor(std::string value) {
    Require((value.size()==7||value.size()==9)&&value[0]=='#',"Invalid color.");unsigned n=0;
    for(size_t i=1;i<value.size();++i) { char c=value[i];int v=c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:c>='A'&&c<='F'?c-'A'+10:-1;Require(v>=0,"Invalid color.");n=(n<<4)|v; }
    if(value.size()==7)n=(n<<8)|255;return {float((n>>24)&255)/255,float((n>>16)&255)/255,float((n>>8)&255)/255,float(n&255)/255};
}
struct Sprite { std::string asset; int x=0,y=0,width=16,height=16; };
inline Sprite ReadSprite(const Json& data) {
    Sprite s;s.asset=data.Get("asset").Text();s.x=data.Get("x").Integer();s.y=data.Get("y").Integer();s.width=data.Get("width").Integer(16);s.height=data.Get("height").Integer(16);
    Require(!s.asset.empty()&&s.x>=0&&s.y>=0&&s.width>0&&s.height>0&&s.width<=16384&&s.height<=16384,"Invalid sprite rectangle.");return s;
}
struct Primitive {
    std::vector<Point> points,uv; Sprite sprite; Color color; int layer=0; bool solid=false,trigger=false;
    std::string objectId,definition;
};
struct Room {
    std::string id,name;int x=0,y=0,width=0,height=0,ppu=16,referenceWidth=320,referenceHeight=180;
    std::vector<Primitive> primitives;
    Json document,catalog,metadata;
};
inline int NormalizeMask(int m) { if((m&5)!=5)m&=~2;if((m&20)!=20)m&=~8;if((m&80)!=80)m&=~32;if((m&65)!=65)m&=~128;return m&255; }
inline bool HasEdge(int shape,int x,int y) { switch(shape) {case 0:return true;case 1:return x<0||y<0;case 2:return x>0||y<0;case 3:return x<0||y>0;case 4:return x>0||y>0;default:return false;} }
inline std::vector<Point> Polygon(int shape) {
    switch(shape) {case 0:return {{0,0},{1,0},{1,1},{0,1}};case 1:return {{0,0},{1,0},{0,1}};case 2:return {{0,0},{1,0},{1,1}};case 3:return {{0,0},{1,1},{0,1}};case 4:return {{1,0},{1,1},{0,1}};default:throw std::runtime_error("Invalid tile shape.");}
}
inline bool Visible(const std::string& id,const std::map<std::string,const Json*>& groups) {
    auto current=id;std::set<std::string> seen;
    while(!current.empty()) { Require(seen.insert(current).second,"Cyclic layer groups.");auto it=groups.find(current);Require(it!=groups.end(),"Missing layer group.");if(!it->second->Get("visible").Bool(true))return false;current=it->second->Get("parentId").Text(); }return true;
}
inline Room LoadRoom(const std::string& mapText,const std::string& catalogText,const std::string& selected="") {
    Room result;result.document=JsonReader(mapText).Read();result.catalog=JsonReader(catalogText).Read();
    Require(result.document.kind==Json::Kind::Object&&result.catalog.kind==Json::Kind::Object,"Expected map and catalog objects.");
    int version=result.document.Get("formatVersion").Integer();Require(version==1||version==2,"Unsupported map format version.");Require(result.document.Get("tileSize").Integer()==16,"Unsupported tile size.");
    const auto& rooms=result.document.Get("rooms").Items();Require(!rooms.empty()&&rooms.size()<=1024,"Invalid room count.");
    std::set<std::string> roomIds;const Json* room=nullptr;
    for(const auto& item:rooms) { auto id=item.Get("id").Text();Require(!id.empty()&&roomIds.insert(id).second,"Duplicate or empty room ID.");if(id==selected||(selected.empty()&&!room))room=&item; }
    Require(room!=nullptr,"Room ID was not found.");result.metadata=*room;result.id=room->Get("id").Text();result.name=room->Get("name").Text(result.id);
    result.x=room->Get("x").Integer();result.y=room->Get("y").Integer();result.width=room->Get("width").Integer();result.height=room->Get("height").Integer();
    Require(result.width>=1&&result.width<=1024&&result.height>=1&&result.height<=1024,"Room dimensions must be between 1 and 1024.");
    const auto& camera=result.catalog.Get("camera");result.ppu=camera.Get("ppu").Integer(16);result.referenceWidth=camera.Get("referenceWidth").Integer(320);result.referenceHeight=camera.Get("referenceHeight").Integer(180);
    for(const auto& property:result.document.Get("properties").Items()) {
        auto key=property.Get("key").Text();int* target=key=="metroidvaniaStudio.camera.ppu"?&result.ppu:key=="metroidvaniaStudio.camera.width"?&result.referenceWidth:key=="metroidvaniaStudio.camera.height"?&result.referenceHeight:nullptr;
        if(target) { auto value=property.Get("value").Text();Require(!value.empty()&&value.size()<=5&&value.find_first_not_of("0123456789")==std::string::npos,"Invalid camera property.");*target=std::stoi(value); }
    }
    Require(result.ppu>=1&&result.ppu<=8192&&result.referenceWidth>=1&&result.referenceWidth<=16384&&result.referenceHeight>=1&&result.referenceHeight<=16384,"Invalid camera profile.");
    std::map<std::string,const Json*> groups,materials,objects;
    for(const auto& item:result.document.Get("layerGroups").Items()) { auto id=item.Get("id").Text();Require(!id.empty()&&groups.emplace(id,&item).second,"Duplicate layer group."); }
    for(const auto& item:result.catalog.Get("materials").Items())Require(materials.emplace(item.Get("id").Text(),&item).second,"Duplicate material.");
    for(const auto& item:result.catalog.Get("objects").Items()) { auto key=item.Get("id").Text();std::transform(key.begin(),key.end(),key.begin(),[](unsigned char c){return c>='A'&&c<='Z'?c+32:c;});Require(objects.emplace(key,&item).second,"Duplicate object definition."); }
    for(const auto& layer: {std::string("background"),std::string("foreground")}) {
        std::map<std::pair<int,int>,const Json*> cells;
        for(const auto& cell:room->Get(layer).Items()) {
            int x=cell.Get("x").Integer(),y=cell.Get("y").Integer(),shape=cell.Get("shape").Integer();
            Require(x>=0&&x<result.width&&y>=0&&y<result.height&&shape>=0&&shape<=4,"Invalid tile cell.");Require(cells.emplace(std::make_pair(x,y),&cell).second,"Duplicate tile cell.");
        }
        for(const auto& entry:cells) {
            const auto& cell=*entry.second;if(!Visible(cell.Get("groupId").Text(),groups))continue;
            int x=entry.first.first,y=entry.first.second,shape=cell.Get("shape").Integer(),mask=0;auto materialId=cell.Get("material").Text("terrain");
            const int dx[]={0,1,1,1,0,-1,-1,-1},dy[]={1,1,0,-1,-1,-1,0,1};
            for(int i=0;i<8;++i) {
                int nx=x+dx[i],ny=y+dy[i];auto found=cells.find({nx,ny});bool present=nx<0||ny<0||nx>=result.width||ny>=result.height;int other=0;
                if(!present&&found!=cells.end()) {const auto& neighbor=*found->second;present=neighbor.Get("material").Text("terrain")==materialId&&Visible(neighbor.Get("groupId").Text(),groups);other=neighbor.Get("shape").Integer();}
                if(present&&HasEdge(shape,dx[i],dy[i])&&HasEdge(other,-dx[i],-dy[i]))mask|=1<<i;
            }
            mask=shape==0?NormalizeMask(mask):0;auto mat=materials.find(materialId);Require(mat!=materials.end(),"Missing material: "+materialId);
            const Json* sprite=nullptr;for(const auto& item:mat->second->Get("sprites").Items())if(item.Get("shape").Integer()==shape) { if(item.Get("mask").Integer()==mask){sprite=&item;break;}if(item.Get("mask").Integer()==0)sprite=&item; }
            Require(sprite!=nullptr,"Missing terrain sprite: "+materialId);Primitive p;p.sprite=ReadSprite(*sprite);Require(p.sprite.width==16&&p.sprite.height==16,"Terrain sprites must be 16 by 16.");
            p.layer=layer=="foreground"?0:-20;p.solid=layer=="foreground";
            for(const auto& point:Polygon(shape)) {p.points.push_back({(x+point.x)*16,(y+point.y)*16});p.uv.push_back(point);}
            result.primitives.push_back(std::move(p));
        }
    }
    std::set<std::string> objectIds;
    for(const auto& object:room->Get("objects").Items()) {
        Require(objectIds.insert(object.Get("id").Text()).second,"Duplicate object ID.");if(!Visible(object.Get("groupId").Text(),groups))continue;
        int layer=object.Get("layer").Integer(2);Require(layer>=2&&layer<=5,"Invalid object layer.");
        double x=object.Get("x").Number(),y=object.Get("y").Number(),w=object.Get("width").Number(1),h=object.Get("height").Number(1),sx=object.Get("scaleX").Number(1),sy=object.Get("scaleY").Number(1),rotation=object.Get("rotation").Number();
        Require(w>0&&h>0&&w<=1048576&&h<=1048576&&std::abs(x)<=2147483647&&std::abs(y)<=2147483647&&std::abs(sx)<=1048576&&std::abs(sy)<=1048576,"Invalid object transform.");
        Primitive p;p.layer=layer==5?-10:layer==4?20:10;p.trigger=layer==3;p.objectId=object.Get("id").Text();p.definition=object.Get("definition").Text("object");
        std::string key=p.definition;std::transform(key.begin(),key.end(),key.begin(),[](unsigned char c){return c>='A'&&c<='Z'?c+32:c;});auto definition=objects.find(key);
        if(definition!=objects.end()) { auto& data=*definition->second;if(data.Get("sprite").kind!=Json::Kind::Null)p.sprite=ReadSprite(data.Get("sprite"));else p.color=ParseColor(data.Get("color").Text("#FFFFFF")); }
        double angle=rotation*3.14159265358979323846/180,c=std::cos(angle),s=std::sin(angle);
        for(const auto& point:Polygon(0)) {double px=(point.x-.5)*w*sx,py=(point.y-.5)*h*sy;p.points.push_back({(x+w*.5+c*px-s*py)*16,(y+h*.5+s*px+c*py)*16});p.uv.push_back(point);}
        result.primitives.push_back(std::move(p));
    }
    std::stable_sort(result.primitives.begin(),result.primitives.end(),[](const Primitive& a,const Primitive& b){return a.layer<b.layer;});return result;
}
}
