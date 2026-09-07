#pragma once
#include "CoreMinimal.h"
#include "Camera/CameraComponent.h"
#include "Blueprint/UserWidget.h"
#include "MetroidvaniaStudioPixelCamera.generated.h"

class USceneCaptureComponent2D;
class UTextureRenderTarget2D;
class UMetroidvaniaStudioPixelView;

UCLASS(ClassGroup=Camera, meta=(BlueprintSpawnableComponent))
class METROIDVANIASTUDIO_API UMetroidvaniaStudioPixelCamera : public UCameraComponent
{
    GENERATED_BODY()
public:
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Pixel View") int32 PixelsPerUnit = 16;
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Pixel View") FIntPoint ReferenceResolution = FIntPoint(320,180);
    UPROPERTY(EditAnywhere, BlueprintReadWrite, Category="Pixel View") float UnitsPerWorldUnit = 100.0f;
    UFUNCTION(BlueprintCallable, Category="Pixel View") UTextureRenderTarget2D* CapturePixels();
    virtual void GetCameraView(float DeltaTime, FMinimalViewInfo& DesiredView) override;
    virtual void OnComponentDestroyed(bool bDestroyingHierarchy) override;
    FVector SnapLocation(const FVector& Position) const;
    UTextureRenderTarget2D* GetPixelTarget() const { return PixelTarget; }
private:
    UPROPERTY(Transient) USceneCaptureComponent2D* PixelCapture = nullptr;
    UPROPERTY(Transient) UTextureRenderTarget2D* PixelTarget = nullptr;
    UPROPERTY(Transient) UMetroidvaniaStudioPixelView* PixelView = nullptr;
};

UCLASS()
class METROIDVANIASTUDIO_API UMetroidvaniaStudioPixelView : public UUserWidget
{
    GENERATED_BODY()
public:
    UPROPERTY(Transient) UMetroidvaniaStudioPixelCamera* Camera = nullptr;
    static FIntRect PixelRect(FIntPoint Available, FIntPoint Reference);
protected:
    virtual void NativeTick(const FGeometry& Geometry, float DeltaTime) override;
    virtual int32 NativePaint(const FPaintArgs& Args, const FGeometry& Geometry, const FSlateRect& CullingRect,
                             FSlateWindowElementList& Elements, int32 Layer, const FWidgetStyle& Style, bool ParentEnabled) const override;
};
