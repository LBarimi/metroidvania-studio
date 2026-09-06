#pragma once
#include "CoreMinimal.h"
#include "GameFramework/Actor.h"
#include "Engine/EngineTypes.h"
#include "MetroidvaniaStudioRoom.generated.h"

class UProceduralMeshComponent;
class UCameraComponent;
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
    UPROPERTY(EditAnywhere, Category="MetroidvaniaStudio|Import", meta=(FilePathFilter="json")) FFilePath MapFile;
    UPROPERTY(EditAnywhere, Category="MetroidvaniaStudio|Import", meta=(FilePathFilter="json")) FFilePath CatalogFile;
    UPROPERTY(EditAnywhere, Category="MetroidvaniaStudio|Import") FDirectoryPath ResourceDirectory;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="MetroidvaniaStudio|Import") FString RoomId;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="MetroidvaniaStudio|Display", meta=(ClampMin="0.001")) float UnitsPerWorldUnit = 100.0f;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="MetroidvaniaStudio") FString LastError;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="MetroidvaniaStudio") FString RoomName;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="MetroidvaniaStudio") int32 PixelsPerUnit = 16;
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="MetroidvaniaStudio") FIntPoint ReferenceResolution = FIntPoint(320,180);
    UPROPERTY(VisibleAnywhere, BlueprintReadOnly, Category="MetroidvaniaStudio") UCameraComponent* RoomCamera;
    UPROPERTY(BlueprintReadOnly, Category="MetroidvaniaStudio") FString MapDocumentJson;
    UPROPERTY() FString CatalogJson;
    UPROPERTY() TMap<FString,FMetroidvaniaStudioImage> Images;
    UPROPERTY() UMaterialInterface* TileMaterial;

    UFUNCTION(CallInEditor, BlueprintCallable, Category="MetroidvaniaStudio") bool ImportRoom();
    UFUNCTION(CallInEditor, BlueprintCallable, Category="MetroidvaniaStudio") bool RebuildRoom();
    UFUNCTION(CallInEditor, BlueprintCallable, Category="MetroidvaniaStudio") void ClearRoom();
    virtual void OnConstruction(const FTransform& Transform) override;
protected:
    virtual void BeginPlay() override;
private:
    UPROPERTY(Transient) TArray<UProceduralMeshComponent*> GeneratedMeshes;
    bool bBuilt = false;
    void DestroyGenerated();
};
