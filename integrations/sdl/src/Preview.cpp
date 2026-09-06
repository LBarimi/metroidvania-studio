#include "StudioSdlRoom.h"
#include <SDL3/SDL_main.h>
#include <iostream>

int main(int argc,char** argv) {
    SDL_Window* window=nullptr;SDL_Renderer* renderer=nullptr;SDL_Surface* target=nullptr;
    auto cleanup=[&]{SDL_DestroyRenderer(renderer);SDL_DestroyWindow(window);SDL_DestroySurface(target);SDL_Quit();};
    try {
        using namespace MetroidvaniaStudio;
        Require(argc>=4,"Usage: preview <map.json> <catalog.json> <resource-directory> [room-id] [--smoke]");
        bool smoke=false;std::string roomId;for(int i=4;i<argc;++i){if(std::string(argv[i])=="--smoke")smoke=true;else roomId=argv[i];}
        auto initial=LoadRoom(ReadText(std::filesystem::u8path(argv[1])),ReadText(std::filesystem::u8path(argv[2])),roomId);
        int width=smoke?initial.width*16:initial.referenceWidth,height=smoke?initial.height*16:initial.referenceHeight;
        if(smoke) {
            Require(SDL_Init(0),SDL_GetError());target=SDL_CreateSurface(width,height,SDL_PIXELFORMAT_RGBA32);Require(target!=nullptr,SDL_GetError());renderer=SDL_CreateSoftwareRenderer(target);
        }else {
            Require(SDL_Init(SDL_INIT_VIDEO),SDL_GetError());window=SDL_CreateWindow("MetroidvaniaStudio - arrows: move, R: reload",width*3,height*3,SDL_WINDOW_RESIZABLE);Require(window!=nullptr,SDL_GetError());renderer=SDL_CreateRenderer(window,nullptr);
        }
        Require(renderer!=nullptr,SDL_GetError());Require(SDL_SetRenderLogicalPresentation(renderer,width,height,SDL_LOGICAL_PRESENTATION_INTEGER_SCALE),SDL_GetError());
        {
            SdlRoom room(renderer);room.Load(std::filesystem::u8path(argv[1]),std::filesystem::u8path(argv[2]),std::filesystem::u8path(argv[3]),roomId);
            Point camera{room.data.width*8.0/room.data.ppu,room.data.height*8.0/room.data.ppu};bool running=true;uint64_t before=SDL_GetTicks();
            while(running) {
                SDL_Event event;while(SDL_PollEvent(&event)) {
                    if(event.type==SDL_EVENT_QUIT||(event.type==SDL_EVENT_KEY_DOWN&&event.key.key==SDLK_ESCAPE))running=false;
                    if(event.type==SDL_EVENT_KEY_DOWN&&event.key.key==SDLK_R) {
                        try {room.Load(std::filesystem::u8path(argv[1]),std::filesystem::u8path(argv[2]),std::filesystem::u8path(argv[3]),roomId);}
                        catch(const std::exception& e){std::cerr<<"Reload failed: "<<e.what()<<'\n';}
                    }
                }
                auto now=SDL_GetTicks();double delta=std::min(double(now-before)/1000,.1);before=now;
                const bool* keys=SDL_GetKeyboardState(nullptr);double speed=160*delta/room.data.ppu;
                camera.x+=(int(keys[SDL_SCANCODE_RIGHT])-int(keys[SDL_SCANCODE_LEFT]))*speed;camera.y+=(int(keys[SDL_SCANCODE_UP])-int(keys[SDL_SCANCODE_DOWN]))*speed;
                SDL_SetRenderDrawColor(renderer,0,0,0,255);SDL_RenderClear(renderer);room.Draw(camera,width,height);SDL_RenderPresent(renderer);
                if(smoke) {
                    Require(SDL_LockSurface(target),SDL_GetError());bool painted=false;
                    for(int y=0;y<height&&!painted;++y){auto* row=static_cast<unsigned char*>(target->pixels)+y*target->pitch;for(int x=0;x<width;++x)if(row[x*4]||row[x*4+1]||row[x*4+2]){painted=true;break;}}
                    SDL_UnlockSurface(target);Require(painted,"Rendered frame was empty.");
                    std::cout<<"SDL import passed: "<<room.data.primitives.size()<<" primitives and a nonempty software-rendered frame.\n";running=false;
                }else SDL_Delay(8);
            }
        }
        cleanup();return 0;
    }catch(const std::exception& error){std::cerr<<error.what()<<'\n';cleanup();return 1;}
}
