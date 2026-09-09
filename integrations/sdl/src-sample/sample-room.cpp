#include "sample-room.h"
#include <algorithm>
#include <cmath>

namespace MetroidvaniaStudio::Sample
{

SampleRoom::SampleRoom(SDL_Renderer *renderer, const RoomFiles &files) : renderer_(renderer), files_(files), room_(renderer)
{
    // 1. Read the room and resolve its textures through the catalog.
    Reload();

    // 2. Start the view at the lower-left room corner. PPU converts pixels to world units.
    camera_ = {room_.data.referenceWidth * 0.5 / room_.data.ppu, room_.data.referenceHeight * 0.5 / room_.data.ppu};
}

void SampleRoom::Reload()
{
    // A failed reload throws and leaves the previous room available for drawing.
    room_.Load(files_.map, files_.catalog, files_.resources, files_.roomId);
}

void SampleRoom::MoveCamera(double horizontal, double vertical, double seconds)
{
    const double distance = 160.0 * std::clamp(seconds, 0.0, 0.1) / room_.data.ppu;
    camera_.x += horizontal * distance;
    camera_.y += vertical * distance; // Positive Y points up in room coordinates.
}

void SampleRoom::Draw(int viewportWidth, int viewportHeight, bool showRoomOutline)
{
    // 3. Draw once per frame, between SDL_RenderClear and SDL_RenderPresent.
    room_.Draw(camera_, viewportWidth, viewportHeight);
    if (showRoomOutline)
    {
        Uint8 r, g, b, a;
        SDL_GetRenderDrawColor(renderer_, &r, &g, &b, &a);
        SDL_SetRenderDrawColor(renderer_, 255, 255, 255, 255);
        const SDL_FRect bounds{
            float(viewportWidth * .5 - std::round(camera_.x * room_.data.ppu)),
            float(viewportHeight * .5 + std::round(camera_.y * room_.data.ppu) - room_.data.height * 16),
            float(room_.data.width * 16), float(room_.data.height * 16)};
        SDL_RenderRect(renderer_, &bounds);
        SDL_SetRenderDrawColor(renderer_, r, g, b, a);
    }
}

}
