#include "MetroidvaniaStudioRoomOutline.h"
#include "PrimitiveSceneProxy.h"
#include "SceneManagement.h"

UMetroidvaniaStudioRoomOutline::UMetroidvaniaStudioRoomOutline()
{
    SetCollisionEnabled(ECollisionEnabled::NoCollision);
    SetHiddenInGame(true);
    SetGenerateOverlapEvents(false);
#if WITH_EDITOR
    SetIsVisualizationComponent(true);
    bIsEditorOnly = true;
#endif
}

FPrimitiveSceneProxy* UMetroidvaniaStudioRoomOutline::CreateSceneProxy()
{
#if WITH_EDITOR
    class FOutlineProxy final : public FPrimitiveSceneProxy
    {
        FVector Extent;
    public:
        explicit FOutlineProxy(const UMetroidvaniaStudioRoomOutline* Component)
            : FPrimitiveSceneProxy(Component), Extent(Component->GetUnscaledBoxExtent()) {}
        virtual SIZE_T GetTypeHash() const override
        {
            static int Identity;
            return reinterpret_cast<SIZE_T>(&Identity);
        }
        virtual void GetDynamicMeshElements(const TArray<const FSceneView*>& Views, const FSceneViewFamily& Family,
                                            uint32 Visibility, FMeshElementCollector& Collector) const override
        {
            if (Family.EngineShowFlags.Game) return;
            const FVector Local[] = {{-Extent.X, 0, -Extent.Z}, {Extent.X, 0, -Extent.Z},
                                     {Extent.X, 0, Extent.Z}, {-Extent.X, 0, Extent.Z}};
            for (int32 ViewIndex = 0; ViewIndex < Views.Num(); ++ViewIndex)
            {
                if (!(Visibility & (1u << ViewIndex))) continue;
                auto* Draw = Collector.GetPDI(ViewIndex);
                for (int32 Edge = 0; Edge < 4; ++Edge)
                    Draw->DrawLine(GetLocalToWorld().TransformPosition(Local[Edge]),
                                   GetLocalToWorld().TransformPosition(Local[(Edge + 1) % 4]),
                                   FLinearColor::White, SDPG_Foreground, 2.0f, 0, true);
            }
        }
        virtual FPrimitiveViewRelevance GetViewRelevance(const FSceneView* View) const override
        {
            FPrimitiveViewRelevance Result;
            Result.bDrawRelevance = IsShown(View) && !View->Family->EngineShowFlags.Game;
            Result.bDynamicRelevance = true;
            Result.bEditorPrimitiveRelevance = UseEditorCompositing(View);
            return Result;
        }
        virtual uint32 GetMemoryFootprint() const override { return sizeof(*this) + GetAllocatedSize(); }
    };
    return new FOutlineProxy(this);
#else
    return nullptr;
#endif
}
