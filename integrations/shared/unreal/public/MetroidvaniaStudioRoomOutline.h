#pragma once
#include "CoreMinimal.h"
#include "Components/BoxComponent.h"
#include "MetroidvaniaStudioRoomOutline.generated.h"

UCLASS()
class METROIDVANIASTUDIO_API UMetroidvaniaStudioRoomOutline : public UBoxComponent
{
    GENERATED_BODY()
public:
    UMetroidvaniaStudioRoomOutline();
    virtual FPrimitiveSceneProxy* CreateSceneProxy() override;
};
