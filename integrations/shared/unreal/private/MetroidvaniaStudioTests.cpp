#if WITH_DEV_AUTOMATION_TESTS
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Interfaces/IPluginManager.h"
#include "Engine/World.h"
#include "MetroidvaniaStudioRoom.h"
#include "StudioDocument.h"
#include "MetroidvaniaStudioTriggerManager.h"
#include "MetroidvaniaStudioPixelCamera.h"
#include "ProceduralMeshComponent.h"
#include "PhysicsEngine/BodySetup.h"

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
    const auto CameraLocation = Room->RoomCamera->GetRelativeLocation();
    const double PixelUnit = Room->UnitsPerWorldUnit / Room->PixelsPerUnit;
    TestTrue(TEXT("Camera view begins at room lower-left"), FMath::IsNearlyEqual(static_cast<double>(CameraLocation.X), Room->ReferenceResolution.X * 0.5 * PixelUnit)
        && FMath::IsNearlyEqual(static_cast<double>(CameraLocation.Z), Room->ReferenceResolution.Y * 0.5 * PixelUnit));
    TestTrue(TEXT("Camera width matches source pixels"), FMath::IsNearlyEqual(static_cast<double>(Room->RoomCamera->OrthoWidth), Room->ReferenceResolution.X * PixelUnit));
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
IMPLEMENT_SIMPLE_AUTOMATION_TEST(FStudioTriggerTest, "MetroidvaniaStudio.Runtime.TriggerRequests", EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)
bool FStudioTriggerTest::RunTest(const FString& Parameters)
{
    const auto Plugin = IPluginManager::Get().FindPlugin(TEXT("MetroidvaniaStudio"));
    FString Json;
    TestTrue(TEXT("Read trigger fixture"), FFileHelper::LoadFileToString(Json, *FPaths::Combine(Plugin->GetBaseDir(), TEXT("samples/maps/TriggerEvents.json"))));
    auto* Manager = NewObject<UMetroidvaniaStudioTriggerManager>();
    TestTrue(TEXT("Register room"), Manager->RegisterRoom(Json, TEXT("trigger-room")));
    FStudioTriggerInfo Info;
    TestTrue(TEXT("Query preserves once"), Manager->GetTrigger(TEXT("once"), Info));
    TestEqual(TEXT("Authored room origin"), Info.RoomPosition, FIntPoint(-7, 3));
    TestTrue(TEXT("Enum 200"), Info.Event == EStudioTriggerEvent::Trigger200);
    TestEqual(TEXT("Description"), Info.Description, FString(TEXT("Open gate")));
    TestTrue(TEXT("First request"), Manager->RequestTrigger(TEXT("once")));
    TestFalse(TEXT("Second request"), Manager->RequestTrigger(TEXT("once")));
    TestTrue(TEXT("Repeat"), Manager->RequestTrigger(TEXT("repeat")) && Manager->RequestTrigger(TEXT("repeat")));
    TestFalse(TEXT("Portal is independent"), Manager->GetTrigger(TEXT("portal"), Info) || Manager->RequestTrigger(TEXT("portal")));
    TestFalse(TEXT("Invisible wall is not an event"), Manager->GetTrigger(TEXT("wall"), Info) || Manager->RequestTrigger(TEXT("wall")));
    TestFalse(TEXT("Legacy event unassigned"), Manager->RequestTrigger(TEXT("legacy")));
    TestTrue(TEXT("Register again"), Manager->RegisterRoom(Json, TEXT("trigger-room")));
    TestFalse(TEXT("Reload keeps consumed event"), Manager->RequestTrigger(TEXT("once")));
    TestTrue(TEXT("Reset once"), Manager->ResetOnce(TEXT("once")) && Manager->RequestTrigger(TEXT("once")));
    Manager->Clear(); TestFalse(TEXT("Clear"), Manager->RequestTrigger(TEXT("once")));
    auto* World = UWorld::CreateWorld(EWorldType::Game, false);
    auto* Room = World->SpawnActor<AMetroidvaniaStudioRoom>();
    Room->MapDocumentJson = Json;
    Room->CatalogJson = TEXT("{\"materials\":[],\"objects\":[]}");
    Room->RoomId = TEXT("trigger-room");
    TestTrue(TEXT("Build independent regions"), Room->RebuildRoom());
    TArray<UProceduralMeshComponent*> Meshes;
    Room->GetComponents(Meshes);
    int32 RegionCount = 0;
    for (auto* Mesh : Meshes)
    {
        const bool Portal = Mesh->ComponentHasTag(TEXT("portal"));
        const bool Wall = Mesh->ComponentHasTag(TEXT("wall"));
        if (!Portal && !Wall) continue;
        ++RegionCount;
        TestTrue(TEXT("Region has collision geometry"), Mesh->GetBodySetup() && Mesh->GetBodySetup()->AggGeom.ConvexElems.Num() > 0);
        TestEqual(TEXT("Region has no rendered surface"), Mesh->GetNumSections(), 0);
        TestTrue(TEXT("Collision mode"), Mesh->GetCollisionEnabled() == (Wall ? ECollisionEnabled::QueryAndPhysics : ECollisionEnabled::QueryOnly));
        TestTrue(TEXT("Player response"), Mesh->GetCollisionResponseToChannel(ECC_Pawn) == (Wall ? ECR_Block : ECR_Overlap));
        TestEqual(TEXT("Overlap notifications"), Mesh->GetGenerateOverlapEvents(), Portal);
    }
    TestEqual(TEXT("Both regions imported"), RegionCount, 2);
    World->DestroyWorld(false);
    return true;
}
#endif
