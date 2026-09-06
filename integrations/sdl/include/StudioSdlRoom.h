#pragma once
#include <SDL3/SDL.h>
#include "StudioDocument.h"
#include <memory>

namespace MetroidvaniaStudio {
class SdlRoom {
    struct TextureDeleter {void operator()(SDL_Texture* p)const{SDL_DestroyTexture(p);}};
    struct Batch {SDL_Texture* texture=nullptr;std::vector<SDL_Vertex> base,screen;std::vector<int> indices;};
    SDL_Renderer* renderer;
    std::map<std::string,std::unique_ptr<SDL_Texture,TextureDeleter>> textures;
    std::vector<Batch> batches;
public:
    Room data;
    explicit SdlRoom(SDL_Renderer* renderer):renderer(renderer){}
    SdlRoom(const SdlRoom&)=delete;
    SdlRoom& operator=(const SdlRoom&)=delete;
    // Throws on invalid input. A failed reload retains the previous room.
    void Load(const std::filesystem::path& map,const std::filesystem::path& catalog,const std::filesystem::path& resources,const std::string& id="");
    // Camera center is room-local world units. One unit contains data.ppu source pixels.
    void Draw(Point camera,int viewportWidth,int viewportHeight);
};
}
