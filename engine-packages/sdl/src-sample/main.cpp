#include "sample-room.h"
#include <SDL3/SDL_main.h>
#include <iostream>
#include <vector>

using MetroidvaniaStudio::Require;
using MetroidvaniaStudio::Sample::RoomFiles;
using MetroidvaniaStudio::Sample::SampleRoom;

namespace
{

RoomFiles ReadArguments(int argc, char **argv, bool &smoke)
{
    std::vector<std::string> arguments;
    for (int i = 1; i < argc; ++i)
    {
        if (std::string(argv[i]) == "--smoke")
            smoke = true;
        else
            arguments.emplace_back(argv[i]);
    }
    Require(arguments.empty() || arguments.size() == 3 || arguments.size() == 4,
            "Usage: preview [<map.json> <catalog.json> <resource-directory> [room-id]] [--smoke]");
    RoomFiles files;
    if (!arguments.empty())
    {
        files.map = std::filesystem::u8path(arguments[0]);
        files.catalog = std::filesystem::u8path(arguments[1]);
        files.resources = std::filesystem::u8path(arguments[2]);
        if (arguments.size() == 4)
            files.roomId = arguments[3];
    }
    return files;
}

void CheckFrame(SDL_Surface *surface)
{
    Require(SDL_LockSurface(surface), SDL_GetError());
    bool painted = false;
    for (int y = 0; y < surface->h && !painted; ++y)
    {
        const auto *row = static_cast<unsigned char *>(surface->pixels) + y * surface->pitch;
        for (int x = 0; x < surface->w; ++x)
        {
            if (row[x * 4] || row[x * 4 + 1] || row[x * 4 + 2])
            {
                painted = true;
                break;
            }
        }
    }
    SDL_UnlockSurface(surface);
    Require(painted, "Rendered frame was empty.");
}

}

int main(int argc, char **argv)
{
    SDL_Window *window = nullptr;
    SDL_Renderer *renderer = nullptr;
    SDL_Surface *surface = nullptr;
    const auto cleanup = [&]
    {
        SDL_DestroyRenderer(renderer);
        SDL_DestroyWindow(window);
        SDL_DestroySurface(surface);
        SDL_Quit();
    };

    try
    {
        bool smoke = false;
        const RoomFiles files = ReadArguments(argc, argv, smoke);
        // Read dimensions before creating SDL's output surface or window.
        const auto initial = MetroidvaniaStudio::LoadRoom(MetroidvaniaStudio::ReadText(files.map),
                                                          MetroidvaniaStudio::ReadText(files.catalog), files.roomId);
        int width = initial.referenceWidth;
        int height = initial.referenceHeight;

        if (smoke)
        {
            // Optional background check: no window or display is needed.
            Require(SDL_Init(0), SDL_GetError());
            surface = SDL_CreateSurface(width, height, SDL_PIXELFORMAT_RGBA32);
            Require(surface != nullptr, SDL_GetError());
            renderer = SDL_CreateSoftwareRenderer(surface);
        }
        else
        {
            Require(SDL_Init(SDL_INIT_VIDEO), SDL_GetError());
            window = SDL_CreateWindow("Metroidvania Studio - arrows: move, R: reload", width * 3, height * 3,
                                      SDL_WINDOW_RESIZABLE);
            Require(window != nullptr, SDL_GetError());
            renderer = SDL_CreateRenderer(window, nullptr);
        }
        Require(renderer != nullptr, SDL_GetError());
        Require(SDL_SetRenderLogicalPresentation(renderer, width, height, SDL_LOGICAL_PRESENTATION_INTEGER_SCALE),
                SDL_GetError());

        {
            // Load after renderer creation. This scope releases room textures first.
            SampleRoom room(renderer, files);
            bool running = true;
            int smokeFrames = 0;
            Uint64 previous = SDL_GetTicks();
            while (running)
            {
                SDL_Event event;
                while (SDL_PollEvent(&event))
                {
                    if (event.type == SDL_EVENT_QUIT ||
                        (event.type == SDL_EVENT_KEY_DOWN && event.key.key == SDLK_ESCAPE))
                        running = false;
                    if (event.type == SDL_EVENT_KEY_DOWN && event.key.key == SDLK_R && !event.key.repeat)
                    {
                        try
                        {
                            room.Reload();
                            if (!smoke)
                            {
                                width = room.Data().referenceWidth;
                                height = room.Data().referenceHeight;
                                Require(SDL_SetRenderLogicalPresentation(renderer, width, height,
                                                                         SDL_LOGICAL_PRESENTATION_INTEGER_SCALE),
                                        SDL_GetError());
                            }
                        }
                        catch (const std::exception &error)
                        {
                            std::cerr << "Reload failed: " << error.what() << '\n';
                        }
                    }
                }
                const Uint64 now = SDL_GetTicks();
                const double seconds = double(now - previous) / 1000.0;
                previous = now;
                if (!smoke)
                {
                    const bool *keys = SDL_GetKeyboardState(nullptr);
                    room.MoveCamera(int(keys[SDL_SCANCODE_RIGHT]) - int(keys[SDL_SCANCODE_LEFT]),
                                    int(keys[SDL_SCANCODE_UP]) - int(keys[SDL_SCANCODE_DOWN]), seconds);
                }

                Require(SDL_SetRenderDrawColor(renderer, 0, 0, 0, 255), SDL_GetError());
                Require(SDL_RenderClear(renderer), SDL_GetError());
                room.Draw(width, height);
                Require(SDL_RenderPresent(renderer), SDL_GetError());

                if (smoke)
                {
                    CheckFrame(surface);
                    if (++smokeFrames == 1)
                        room.Reload();
                    else
                    {
                        std::cout << "SDL sample passed: load, reload and two nonempty frames; "
                                  << room.Data().primitives.size() << " primitives.\n";
                        running = false;
                    }
                }
                else
                    SDL_Delay(8);
            }
        }
        cleanup();
        return 0;
    }
    catch (const std::exception &error)
    {
        std::cerr << error.what() << '\n';
        cleanup();
        return 1;
    }
}
