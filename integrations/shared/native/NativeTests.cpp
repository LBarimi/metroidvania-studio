#include "StudioDocument.h"
#include <iostream>
using namespace MetroidvaniaStudio;
int main(int argc,char** argv) {
 try {
  Require(argc==3||argc==4,"Pass a map and catalog.");auto map=ReadText(std::filesystem::u8path(argv[1])),catalog=ReadText(std::filesystem::u8path(argv[2]));auto room=LoadRoom(map,catalog);
  Require(!room.primitives.empty()&&room.ppu==16,"Sample room did not load.");
  auto movement = LoadRoom(R"({"formatVersion":2,"tileSize":16,"rooms":[{"id":"movement","width":4,"height":4,"foreground":[{"x":1,"y":1,"material":"traversal-ladder"},{"x":2,"y":1,"material":"traversal-floor"}]}]})",catalog);
  double landing = 0;
  Require(movement.traversal.IsClimbable({1.5,1.5}) && !movement.traversal.IsClimbable({2.5,1.5}), "Climbing requires a ladder.");
  Require(movement.traversal.FindLanding({2.5,3},{2.5,0},false,landing) && landing==2, "Land on the floor from above.");
  Require(!movement.traversal.FindLanding({2.5,0},{2.5,3},false,landing) && !movement.traversal.FindLanding({2.5,3},{2.5,0},true,landing), "Jump up and drop through the floor.");
  for(const auto& primitive:movement.primitives) Require(!primitive.solid, "Traversal tiles must stay out of solid terrain.");
  int masks=0;for(int i=0;i<256;++i)if(NormalizeMask(i)==i)++masks;Require(masks==47,"Invalid normalized masks.");
  for(const auto& invalid:{"{\"x\":1,\"x\":2}","[01]","[1,]","[true false]","{\"x\":NaN}","1e999","\"\\ud800\""}) {bool rejected=false;try{JsonReader(invalid).Read();}catch(const std::exception&){rejected=true;}Require(rejected,"Invalid JSON was accepted.");}
  Require(JsonReader("\"\\uac00\\ud83d\\ude00\"").Read().Text()=="\xea\xb0\x80\xf0\x9f\x98\x80","Unicode decoding failed.");
  bool rejected=false;try{LoadRoom(map,catalog,"missing-room");}catch(const std::exception&){rejected=true;}Require(rejected,"Missing room was accepted.");
  for(const auto& traversal:{"../outside.png","/outside.png","sub/../../outside.png","scheme:asset"}) {rejected=false;try{ResourcePath(".",traversal);}catch(const std::exception&){rejected=true;}Require(rejected,"Unsafe asset path was accepted.");}
  if(argc==4) {
   auto coverage=LoadRoom(ReadText(std::filesystem::u8path(argv[3])),catalog);
   Require(coverage.ppu==32&&coverage.referenceWidth==640&&coverage.referenceHeight==360,"Camera overrides failed.");
   Require(coverage.x==-7&&coverage.y==3&&coverage.primitives.size()==8,"Coverage room geometry mismatch.");
   int triangles=0,solids=0,triggers=0;for(const auto& p:coverage.primitives){if(p.points.size()==3)++triangles;if(p.solid)++solids;if(p.trigger)++triggers;}
   Require(triangles==4&&solids==5&&triggers==1,"Slopes, hidden groups or triggers failed.");
   Require(coverage.metadata.Get("objects").Items()[0].Get("properties").Items()[0].Get("value").Text()=="preserved","Object metadata lost.");
  }
  std::cout<<"Native import passed: "<<room.primitives.size()<<" primitives, 47 masks, strict JSON and resource checks.\n";return 0;
 }catch(const std::exception& e){std::cerr<<e.what()<<'\n';return 1;}
}
