#if WITH_DEV_AUTOMATION_TESTS
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Interfaces/IPluginManager.h"
#include "Engine/World.h"
#include "MetroidvaniaStudioRoom.h"
#include "StudioDocument.h"
#include "MetroidvaniaStudioPixelCamera.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(FStudioImportTest,"MetroidvaniaStudio.Import.Room",EAutomationTestFlags::EditorContext|EAutomationTestFlags::EngineFilter)
bool FStudioImportTest::RunTest(const FString& Parameters)
{
    const auto Plugin=IPluginManager::Get().FindPlugin(TEXT("MetroidvaniaStudio"));
    if(!Plugin.IsValid()){AddError(TEXT("Plugin not found."));return false;}
    const FString Samples=FPaths::Combine(Plugin->GetBaseDir(),TEXT("samples"));
    auto* World=UWorld::CreateWorld(EWorldType::Game,false);
    auto* Room=World->SpawnActor<AMetroidvaniaStudioRoom>();
    Room->MapFile.FilePath=FPaths::Combine(Samples,TEXT("maps/Sample.map.json"));
    Room->CatalogFile.FilePath=FPaths::Combine(Samples,TEXT("catalog.json"));Room->ResourceDirectory.Path=Samples;
    TestTrue(TEXT("Import sample"),Room->ImportRoom());TestEqual(TEXT("PPU"),Room->PixelsPerUnit,16);
    TestTrue(TEXT("Embedded images"),Room->Images.Num()>0);TestTrue(TEXT("Forget local source paths"),Room->MapFile.FilePath.IsEmpty());
    TestTrue(TEXT("Rebuild embedded data"),Room->RebuildRoom());
    Room->ClearRoom();TestTrue(TEXT("Clear data"),Room->MapDocumentJson.IsEmpty());
    int32 Count=0;for(int32 Mask=0;Mask<256;++Mask)if(MetroidvaniaStudio::NormalizeMask(Mask)==Mask)++Count;
    TestEqual(TEXT("Normalized masks"),Count,47);
    World->DestroyWorld(false);return true;
}
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FStudioPixelAlignmentTest, "MetroidvaniaStudio.Camera.PixelAlignment", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FStudioPixelAlignmentTest::RunTest(const FString& Parameters)
{
    auto* Camera = NewObject<UMetroidvaniaStudioPixelCamera>();
    const FVector Center(1000, 1000, 600);
    TestTrue(TEXT("Subpixel motion keeps the same view"), Camera->SnapLocation(Center + FVector(1, 0, 1)).Equals(Center));
    Camera->PixelsPerUnit = 32;
    TestTrue(TEXT("PPU controls the source pixel grid"), Camera->SnapLocation(FVector(3.125, 100, 3.125)).Equals(FVector(3.125, 100, 3.125)));
    Camera->ReferenceResolution = FIntPoint(321, 181);
    const FVector Odd = Camera->SnapLocation(FVector::ZeroVector);
    TestTrue(TEXT("Odd resolution aligns viewport edges"), FMath::IsNearlyEqual(static_cast<double>(FMath::Frac(FMath::Abs(Odd.X / 3.125))), 0.5));
    TestTrue(TEXT("Uneven window uses centered integer scaling"), UMetroidvaniaStudioPixelView::PixelRect(FIntPoint(1000,700), FIntPoint(320,180)) == FIntRect(20,80,980,620));
    TestTrue(TEXT("Small window crops without fractional downscaling"), UMetroidvaniaStudioPixelView::PixelRect(FIntPoint(200,100), FIntPoint(320,180)) == FIntRect(-60,-40,260,140));
    return true;
}
#endif
