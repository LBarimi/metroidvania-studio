#pragma once
#include "CoreMinimal.h"
#include "Components/ActorComponent.h"
#include "MetroidvaniaStudioTriggerEvent.h"
#include "MetroidvaniaStudioTriggerManager.generated.h"

USTRUCT(BlueprintType)
struct METROIDVANIASTUDIO_API FStudioTriggerInfo
{
    GENERATED_BODY()
    UPROPERTY(BlueprintReadOnly, Category="Metroidvania Studio") FString RoomId;
    UPROPERTY(BlueprintReadOnly, Category="Metroidvania Studio") FString RoomName;
    UPROPERTY(BlueprintReadOnly, Category="Metroidvania Studio") FIntPoint RoomPosition = FIntPoint::ZeroValue;
    UPROPERTY(BlueprintReadOnly, Category="Metroidvania Studio") FString ObjectId;
    UPROPERTY(BlueprintReadOnly, Category="Metroidvania Studio") FString Definition;
    UPROPERTY(BlueprintReadOnly, Category="Metroidvania Studio") EStudioTriggerEvent Event = EStudioTriggerEvent::None;
    UPROPERTY(BlueprintReadOnly, Category="Metroidvania Studio") bool Once = false;
    UPROPERTY(BlueprintReadOnly, Category="Metroidvania Studio") FString Description;
};

DECLARE_DYNAMIC_MULTICAST_DELEGATE_OneParam(FStudioTriggerRequested, const FStudioTriggerInfo&, Info);

UCLASS(ClassGroup=(MetroidvaniaStudio), meta=(BlueprintSpawnableComponent))
class METROIDVANIASTUDIO_API UMetroidvaniaStudioTriggerManager : public UActorComponent
{
    GENERATED_BODY()
public:
    UMetroidvaniaStudioTriggerManager();
    virtual ~UMetroidvaniaStudioTriggerManager() override;
    UPROPERTY(BlueprintAssignable, Category="Metroidvania Studio") FStudioTriggerRequested OnTriggerRequested;
    UPROPERTY(BlueprintReadOnly, Category="Metroidvania Studio") FString LastError;
    UFUNCTION(BlueprintCallable, Category="Metroidvania Studio") bool RegisterRoom(const FString& DocumentJson, const FString& RoomId);
    UFUNCTION(BlueprintCallable, Category="Metroidvania Studio") void UnregisterRoom(const FString& RoomId);
    UFUNCTION(BlueprintCallable, Category="Metroidvania Studio") bool GetTrigger(const FString& ObjectId, FStudioTriggerInfo& Info);
    UFUNCTION(BlueprintCallable, Category="Metroidvania Studio") bool RequestTrigger(const FString& ObjectId);
    UFUNCTION(BlueprintCallable, Category="Metroidvania Studio") bool ResetOnce(const FString& ObjectId);
    UFUNCTION(BlueprintCallable, Category="Metroidvania Studio") void ResetAllOnce();
    UFUNCTION(BlueprintCallable, Category="Metroidvania Studio") void Clear();
private:
    UPROPERTY() TMap<FString, FString> RegisteredRooms;
    struct FState;
    struct FStateDeleter { void operator()(FState* Pointer) const; };
    TUniquePtr<FState, FStateDeleter> State;
    void EnsureState();
};
