#include "StudioSdlRoom.h"

namespace MetroidvaniaStudio {
void SdlRoom::Load(const std::filesystem::path& map,const std::filesystem::path& catalog,const std::filesystem::path& resources,const std::string& id) {
    SdlRoom next(renderer);next.data=LoadRoom(ReadText(map),ReadText(catalog),id);
    std::vector<Batch> grouped;
    std::pair<int,std::string> previous;
    for(const auto& p:next.data.primitives) {
        if(p.trigger)continue;
        SDL_Texture* texture=nullptr;float textureWidth=1,textureHeight=1;
        if(!p.sprite.asset.empty()) {
            auto found=next.textures.find(p.sprite.asset);
            if(found==next.textures.end()) {
                const auto path=ResourcePath(resources,p.sprite.asset);Require(std::filesystem::file_size(path)<=32*1024*1024,"Texture exceeds 32 MiB.");
                std::unique_ptr<SDL_Surface,decltype(&SDL_DestroySurface)> surface(SDL_LoadPNG(path.u8string().c_str()),SDL_DestroySurface);
                Require(surface!=nullptr&&surface->w<=16384&&surface->h<=16384,std::string("Cannot load PNG: ")+SDL_GetError());
                std::unique_ptr<SDL_Texture,TextureDeleter> value(SDL_CreateTextureFromSurface(renderer,surface.get()));
                Require(value!=nullptr,SDL_GetError());SDL_SetTextureScaleMode(value.get(),SDL_SCALEMODE_NEAREST);SDL_SetTextureBlendMode(value.get(),SDL_BLENDMODE_BLEND);
                found=next.textures.emplace(p.sprite.asset,std::move(value)).first;
            }
            texture=found->second.get();Require(SDL_GetTextureSize(texture,&textureWidth,&textureHeight),SDL_GetError());
            Require(double(p.sprite.x)+p.sprite.width<=textureWidth&&double(p.sprite.y)+p.sprite.height<=textureHeight,"Sprite rectangle is outside texture.");
        }
        const auto key=std::make_pair(p.layer,p.sprite.asset);
        if(grouped.empty()||key!=previous) {grouped.emplace_back();previous=key;}
        auto& batch=grouped.back();batch.texture=texture;int base=static_cast<int>(batch.base.size());
        for(size_t i=0;i<p.points.size();++i) {
            SDL_Vertex v{};v.position={float(p.points[i].x),float(-p.points[i].y)};v.color={p.color.r,p.color.g,p.color.b,p.color.a};
            v.tex_coord={float((p.sprite.x+p.uv[i].x*p.sprite.width)/textureWidth),float(1-(p.sprite.y+p.uv[i].y*p.sprite.height)/textureHeight)};batch.base.push_back(v);
        }
        for(int i=1;i<static_cast<int>(p.points.size())-1;++i) {batch.indices.push_back(base);batch.indices.push_back(base+i);batch.indices.push_back(base+i+1);}
    }
    for(auto& batch:grouped) {batch.screen=batch.base;next.batches.push_back(std::move(batch));}
    data=std::move(next.data);textures=std::move(next.textures);batches=std::move(next.batches);
}
void SdlRoom::Draw(Point camera,int viewportWidth,int viewportHeight) {
    const float offsetX=float(viewportWidth*.5-std::round(camera.x*data.ppu));
    const float offsetY=float(viewportHeight*.5+std::round(camera.y*data.ppu));
    for(auto& batch:batches) {
        for(size_t i=0;i<batch.base.size();++i) {batch.screen[i].position.x=batch.base[i].position.x+offsetX;batch.screen[i].position.y=batch.base[i].position.y+offsetY;}
        Require(SDL_RenderGeometry(renderer,batch.texture,batch.screen.data(),static_cast<int>(batch.screen.size()),batch.indices.data(),static_cast<int>(batch.indices.size())),SDL_GetError());
    }
}
}
