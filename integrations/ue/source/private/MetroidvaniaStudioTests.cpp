#if WITH_DEV_AUTOMATION_TESTS
#include "Misc/AutomationTest.h"
#include "Misc/FileHelper.h"
#include "Misc/Paths.h"
#include "Interfaces/IPluginManager.h"
#include "Engine/World.h"
#include "MetroidvaniaStudioRoom.h"
#include "StudioDocument.h"

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
#endif
