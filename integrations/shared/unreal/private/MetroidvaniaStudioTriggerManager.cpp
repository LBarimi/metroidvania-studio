#include "MetroidvaniaStudioTriggerManager.h"
#include "StudioTriggerManager.h"

struct UMetroidvaniaStudioTriggerManager::FState
{
    MetroidvaniaStudio::TriggerManager Manager;
};

void UMetroidvaniaStudioTriggerManager::FStateDeleter::operator()(FState* Pointer) const
{
    delete Pointer;
}

namespace
{
    std::string Utf8(const FString& Text)
    {
        return std::string(TCHAR_TO_UTF8(*Text));
    }

    FStudioTriggerInfo Convert(const MetroidvaniaStudio::TriggerInfo& Source)
    {
        FStudioTriggerInfo Result;
        Result.RoomId = UTF8_TO_TCHAR(Source.roomId.c_str());
        Result.RoomName = UTF8_TO_TCHAR(Source.roomName.c_str());
        Result.RoomPosition = FIntPoint(Source.roomX, Source.roomY);
        Result.ObjectId = UTF8_TO_TCHAR(Source.objectId.c_str());
        Result.Definition = UTF8_TO_TCHAR(Source.definition.c_str());
        Result.Event = static_cast<EStudioTriggerEvent>(Source.event);
        Result.Once = Source.once;
        Result.Description = UTF8_TO_TCHAR(Source.description.c_str());
        return Result;
    }
}

UMetroidvaniaStudioTriggerManager::UMetroidvaniaStudioTriggerManager()
{
    PrimaryComponentTick.bCanEverTick = false;
}

UMetroidvaniaStudioTriggerManager::~UMetroidvaniaStudioTriggerManager() = default;

void UMetroidvaniaStudioTriggerManager::EnsureState()
{
    if (State) return;
    State.Reset(new FState());
    const auto Saved = RegisteredRooms;
    for (const auto& Entry : Saved) RegisterRoom(Entry.Value, Entry.Key);
}

bool UMetroidvaniaStudioTriggerManager::RegisterRoom(const FString& DocumentJson, const FString& RoomId)
{
    EnsureState();
    try
    {
        const auto Document = MetroidvaniaStudio::JsonReader(Utf8(DocumentJson)).Read();
        const int Version = Document.Get("formatVersion").Integer();
        MetroidvaniaStudio::Require((Version == 1 || Version == 2) && Document.Get("tileSize").Integer() == 16, "Unsupported map format.");
        MetroidvaniaStudio::Room Room;
        for (const auto& Item : Document.Get("rooms").Items())
        {
            if (Item.Get("id").Text() != Utf8(RoomId)) continue;
            MetroidvaniaStudio::Require(Room.id.empty(), "Duplicate room ID.");
            Room.id = Item.Get("id").Text();
            Room.name = Item.Get("name").Text();
            Room.x = Item.Get("x").Integer();
            Room.y = Item.Get("y").Integer();
            Room.metadata = Item;
        }
        State->Manager.RegisterRoom(Room);
        RegisteredRooms.Add(RoomId, DocumentJson);
        LastError.Empty();
        return true;
    }
    catch (const std::exception& Error)
    {
        LastError = UTF8_TO_TCHAR(Error.what());
        return false;
    }
}

void UMetroidvaniaStudioTriggerManager::UnregisterRoom(const FString& RoomId)
{
    EnsureState();
    State->Manager.UnregisterRoom(Utf8(RoomId));
    RegisteredRooms.Remove(RoomId);
}

bool UMetroidvaniaStudioTriggerManager::GetTrigger(const FString& ObjectId, FStudioTriggerInfo& Info)
{
    EnsureState();
    MetroidvaniaStudio::TriggerInfo Native;
    const bool Found = State->Manager.TryGet(Utf8(ObjectId), Native);
    Info = Convert(Native);
    return Found;
}

bool UMetroidvaniaStudioTriggerManager::RequestTrigger(const FString& ObjectId)
{
    EnsureState();
    MetroidvaniaStudio::TriggerInfo Native;
    if (!State->Manager.TryRequest(Utf8(ObjectId), Native)) return false;
    const auto Info = Convert(Native);
    OnTriggerRequested.Broadcast(Info);
    return true;
}

bool UMetroidvaniaStudioTriggerManager::ResetOnce(const FString& ObjectId)
{
    EnsureState();
    return State->Manager.ResetOnce(Utf8(ObjectId));
}

void UMetroidvaniaStudioTriggerManager::ResetAllOnce()
{
    EnsureState();
    State->Manager.ResetAllOnce();
}

void UMetroidvaniaStudioTriggerManager::Clear()
{
    RegisteredRooms.Reset();
    State.Reset(new FState());
    LastError.Empty();
}
