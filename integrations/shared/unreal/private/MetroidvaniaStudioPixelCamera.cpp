#include "MetroidvaniaStudioPixelCamera.h"
#include "Runtime/Launch/Resources/Version.h"
#include "Components/SceneCaptureComponent2D.h"
#include "Engine/TextureRenderTarget2D.h"
#include "Engine/World.h"
#include "GameFramework/PlayerController.h"
#include "Kismet/GameplayStatics.h"
#include "Rendering/DrawElements.h"
#include "Styling/CoreStyle.h"

FVector UMetroidvaniaStudioPixelCamera::SnapLocation(const FVector& Position) const
{
    const double Unit = FMath::Max(0.001f, UnitsPerWorldUnit) / FMath::Max(1, PixelsPerUnit);
    const FVector Half(ReferenceResolution.X * 0.5, 0, ReferenceResolution.Y * 0.5);
    return FVector((FMath::RoundToDouble(Position.X / Unit - Half.X) + Half.X) * Unit,
                   Position.Y, (FMath::RoundToDouble(Position.Z / Unit - Half.Z) + Half.Z) * Unit);
}

UTextureRenderTarget2D* UMetroidvaniaStudioPixelCamera::CapturePixels()
{
    if (!GetWorld()) return nullptr;
    const int32 Width = FMath::Clamp(ReferenceResolution.X, 1, 16384);
    const int32 Height = FMath::Clamp(ReferenceResolution.Y, 1, 16384);
    if (!PixelTarget)
    {
        PixelTarget = NewObject<UTextureRenderTarget2D>(this);
        PixelTarget->Filter = TF_Nearest;
        PixelTarget->RenderTargetFormat = RTF_RGBA8;
        PixelTarget->ClearColor = FLinearColor::Black;
        PixelTarget->bAutoGenerateMips = false;
    }
    if (PixelTarget->SizeX != Width || PixelTarget->SizeY != Height)
    {
        PixelTarget->InitCustomFormat(Width, Height, PF_B8G8R8A8, true);
        PixelTarget->UpdateResourceImmediate(true);
    }
    if (!PixelCapture)
    {
        PixelCapture = NewObject<USceneCaptureComponent2D>(GetOwner());
        PixelCapture->bCaptureEveryFrame = false;
        PixelCapture->bCaptureOnMovement = false;
        PixelCapture->ProjectionType = ECameraProjectionMode::Orthographic;
        PixelCapture->CaptureSource = ESceneCaptureSource::SCS_FinalColorLDR;
        TArray<FEngineShowFlagsSetting> Flags;
        for (const TCHAR* Name : {TEXT("TemporalAA"), TEXT("AntiAliasing"), TEXT("MotionBlur"), TEXT("Tonemapper"), TEXT("EyeAdaptation"), TEXT("Bloom"), TEXT("Fog"), TEXT("Atmosphere")})
        {
            FEngineShowFlagsSetting Flag;
            Flag.ShowFlagName = Name;
            Flag.Enabled = false;
            Flags.Add(Flag);
        }
#if ENGINE_MAJOR_VERSION >= 5
        PixelCapture->SetShowFlagSettings(Flags);
#else
        PixelCapture->ShowFlagSettings = Flags;
#endif
        PixelCapture->RegisterComponent();
    }
    PixelCapture->TextureTarget = PixelTarget;
    PixelCapture->OrthoWidth = Width * FMath::Max(0.001f, UnitsPerWorldUnit) / FMath::Max(1, PixelsPerUnit);
    PixelCapture->SetWorldLocationAndRotation(SnapLocation(GetComponentLocation()), GetComponentQuat());
    PixelCapture->CaptureScene();
    return PixelTarget;
}

void UMetroidvaniaStudioPixelCamera::GetCameraView(float DeltaTime, FMinimalViewInfo& DesiredView)
{
    Super::GetCameraView(DeltaTime, DesiredView);
    DesiredView.Location = SnapLocation(DesiredView.Location);
    if (!GetWorld() || !GetWorld()->IsGameWorld()) return;
    auto* Player = UGameplayStatics::GetPlayerController(this, 0);
    if (!Player || !Player->IsLocalController() || Player->GetViewTarget() != GetOwner()) return;
    if (!PixelView || !PixelView->IsInViewport())
    {
        PixelView = CreateWidget<UMetroidvaniaStudioPixelView>(Player);
        if (PixelView)
        {
            PixelView->Camera = this;
            PixelView->SetVisibility(ESlateVisibility::HitTestInvisible);
            PixelView->ForceVolatile(true);
            PixelView->AddToViewport(-1000);
        }
    }
}

void UMetroidvaniaStudioPixelCamera::OnComponentDestroyed(bool bDestroyingHierarchy)
{
    if (PixelView) PixelView->RemoveFromParent();
    if (PixelCapture) PixelCapture->DestroyComponent();
    PixelView = nullptr; PixelCapture = nullptr; PixelTarget = nullptr;
    Super::OnComponentDestroyed(bDestroyingHierarchy);
}

FIntRect UMetroidvaniaStudioPixelView::PixelRect(FIntPoint Available, FIntPoint Reference)
{
    Reference.X = FMath::Max(1, Reference.X); Reference.Y = FMath::Max(1, Reference.Y);
    const int32 Scale = FMath::Max(1, FMath::Min(Available.X / Reference.X, Available.Y / Reference.Y));
    const FIntPoint Size = Reference * Scale;
    const FIntPoint Origin(FMath::FloorToInt((Available.X - Size.X) * 0.5), FMath::FloorToInt((Available.Y - Size.Y) * 0.5));
    return FIntRect(Origin, Origin + Size);
}

void UMetroidvaniaStudioPixelView::NativeTick(const FGeometry& Geometry, float DeltaTime)
{
    Super::NativeTick(Geometry, DeltaTime);
    auto* Player = GetOwningPlayer();
    if (!Camera || !Player || Player->GetViewTarget() != Camera->GetOwner()) { RemoveFromParent(); return; }
    Camera->CapturePixels();
}

int32 UMetroidvaniaStudioPixelView::NativePaint(const FPaintArgs& Args, const FGeometry& Geometry, const FSlateRect& CullingRect,
                                              FSlateWindowElementList& Elements, int32 Layer, const FWidgetStyle& Style, bool ParentEnabled) const
{
    Layer = Super::NativePaint(Args, Geometry, CullingRect, Elements, Layer, Style, ParentEnabled);
    if (!Camera || !Camera->GetPixelTarget()) return Layer;
    const float Dpi = FMath::Max(0.001f, Geometry.GetAccumulatedLayoutTransform().GetScale());
    const FVector2D Physical = Geometry.GetLocalSize() * Dpi;
    const FIntRect Rect = PixelRect(FIntPoint(FMath::RoundToInt(Physical.X), FMath::RoundToInt(Physical.Y)), Camera->ReferenceResolution);
    FSlateDrawElement::MakeBox(Elements, ++Layer, Geometry.ToPaintGeometry(), FCoreStyle::Get().GetBrush("WhiteBrush"), ESlateDrawEffect::None, FLinearColor::Black);
    FSlateBrush Brush;
    Brush.DrawAs = ESlateBrushDrawType::Image;
    Brush.SetResourceObject(Camera->GetPixelTarget());
    Brush.ImageSize = FVector2D(Camera->ReferenceResolution);
    FSlateDrawElement::MakeBox(Elements, ++Layer,
        Geometry.ToPaintGeometry(FVector2D(Rect.Size()) / Dpi, FSlateLayoutTransform(FVector2D(Rect.Min) / Dpi)),
        &Brush, ESlateDrawEffect::NoGamma, FLinearColor::White);
    return Layer;
}
