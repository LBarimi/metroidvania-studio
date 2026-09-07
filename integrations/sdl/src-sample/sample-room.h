#pragma once

#include "StudioSdlRoom.h"
#include <filesystem>
#include <string>

namespace MetroidvaniaStudio::Sample
{

// JSON, its matching catalog, and the folder containing the catalog's textures.
struct RoomFiles
{
    std::filesystem::path map = "samples/maps/Sample.map.json";
    std::filesystem::path catalog = "samples/catalog.json";
    std::filesystem::path resources = "samples";
    std::string roomId; // Empty selects the first room in the JSON.
};

class SampleRoom
{
public:
    // The renderer must already exist and must outlive this object.
    SampleRoom(SDL_Renderer *renderer, const RoomFiles &files);

    void Reload();
    void MoveCamera(double horizontal, double vertical, double seconds);
    void Draw(int viewportWidth, int viewportHeight, bool showRoomOutline = true);
    const Room &Data() const
    {
        return room_.data;
    }

private:
    SDL_Renderer *renderer_;
    RoomFiles files_;
    SdlRoom room_;
    Point camera_{};
};

}
