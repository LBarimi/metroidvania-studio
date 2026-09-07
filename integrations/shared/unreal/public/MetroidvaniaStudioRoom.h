#pragma once
#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "Engine/EngineTypes.h"
#include "MetroidvaniaStudioRoom.generated.h"

class UProceduralMeshComponent;
class UCameraComponent;
class UMetroidvaniaStudioRoomOutline;
class UMaterialInterface;

USTRUCT()
struct FMetroidvaniaStudioImage
{
    GENERATED_BODY()
    UPROPERTY() TArray<uint8> Png;
};

UCLASS(BlueprintType, Blueprintable)
class METROIDVANIASTUDIO_API AMetroidvaniaStudioRoom : public AActor
{
    GENERATED_BODY()
public:
    AMetroidvaniaStudioRoom();
    // Select these in Details, then press Import Room. Imported data is saved in the level.
    UPROPERTY(EditAnywhere, Category="Metroidvania Studio|Import", meta=(FilePathFilter="json")) FFilePath MapFile;
    UPROPERTY(EditAnywhere, Category="Metroidvania Studio|Import", meta=(FilePathFilter="json")) FFilePath CatalogFile;
    UPROPERTY(EditAnywhere, Category="Metroidvania Studio|Import") FDirectoryPath ResourceDirectory;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Metroidvania Studio|Import") FString RoomId;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Metroidvania Studio|Display", meta=(ClampMin="0.001")) float UnitsPerWorldUnit = 100.0f;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Metroidvania Studio") FString LastError;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Metroidvania Studio") FString RoomName;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Metroidvania Studio") int32 PixelsPerUnit = 16;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Metroidvania Studio") FIntPoint ReferenceResolution = FIntPoint(320,180);
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="Metroidvania Studio") UCameraComponent* RoomCamera;
    UPROPERTY(BlueprintReadOnly, Category="Metroidvania Studio") FString MapDocumentJson;
    UPROPERTY() FString CatalogJson;
    UPROPERTY() TMap<FString,FMetroidvaniaStudioImage> Images;
    UPROPERTY() UMaterialInterface* TileMaterial;

    UFUNCTION(CallInEditor, BlueprintCallable, Category="Metroidvania Studio") bool ImportRoom();
    UFUNCTION(CallInEditor, BlueprintCallable, Category="Metroidvania Studio") bool RebuildRoom();
    UFUNCTION(CallInEditor, BlueprintCallable, Category="Metroidvania Studio") void ClearRoom();
    virtual void OnConstruction(const FTransform& Transform) override;
protected:
    virtual void BeginPlay() override;
private:
#if WITH_EDITORONLY_DATA
    UPROPERTY() UMetroidvaniaStudioRoomOutline* RoomOutline = nullptr;
#endif
    UPROPERTY(Transient) TArray<UProceduralMeshComponent*> GeneratedMeshes;
    bool bBuilt = false;
    void DestroyGenerated();
};
