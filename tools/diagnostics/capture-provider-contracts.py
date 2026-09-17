"""Maintainer capture of reviewed provider identities. Does NOT approve runtime adapters.
Run only after reviewing the corresponding installed source/IL; commit the resulting diff for review.
"""
import hashlib, json
from pathlib import Path
game=Path(r'D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\Modules')
workshop=Path(r'D:\Program Files (x86)\Steam\steamapps\workshop\content\261550')
folders={'TAOM':game/'TAOM','TAOM.CoopCompat':game/'TAOM.CoopCompat','CoopModPatch':workshop/'3786391685',
         'ImprovedGarrisons':workshop/'2859265386','MyLittleWarband':workshop/'3627538517',
         'Europe1100':workshop/'2968204274','CoopNightly':workshop/'3770450698'}
def req(module,name):
    records=[]
    for side in ('Client','Server'):
        p=folders[module]/'bin'/('Win64_Shipping_'+side)/name
        if not p.exists(): p=folders[module]/'bin'/'Win64_Shipping_Client'/name
        records.append(dict(Module=module,Name=Path(name).name,Sha256=hashlib.sha256(p.read_bytes()).hexdigest().upper(),Side=side))
    return records
def contract(id,module,operation,provider,targets,requires,suppressed=False,adapter=None):
    return dict(Id=id,Version='1',Module=module,Operation=operation,Provider=provider,Authority='Campaign',
        ActorBinding='Authenticated Coop player registry; operation-specific ownership',Effects=['campaign-mutation'],
        Transport='External provider protocol' if adapter is None else 'Coop Hero.Gold and Clan._influence AutoSync; integration validation required',
        Lifecycle='Before campaign callbacks; immutable for the session',Requires=sum([req(*r) for r in requires],[]),Targets=targets,
        Suppressed=suppressed,OfflineValidated=False,RuntimeValidated=False,AdapterId=adapter)
taom=[('TAOM','TAOM.dll'),('TAOM.CoopCompat','TAOM.CoopCompat.dll')]
contracts=[
    contract('taom.careers','TAOM','Career choices and switching','TAOM.CoopCompat',['TAOM.Features.CareerSystem.CareerDataService::TryAddChoice','TAOM.Features.CareerSystem.CareerSwitchService::SwitchCareer'],taom),
    contract('taom.emissary','TAOM','Elite emissary purchases','TAOM.CoopCompat',['TAOM.Features.EliteEmissary.EliteEmissaryInquiryPresenter::OpenTroopList'],taom),
    contract('taom.camp-suppressed','TAOM','Make Camp','TAOM.CoopCompat',['TAOM.Features.FieldCamp.UI.FieldCampOverlayVM::ExecuteOpenCampMenu'],taom,True),
    contract('ig.management','ImprovedGarrisons','Garrison management','CoopModPatch',['ImprovedGarrisons.SaveSystem.GarrisonBehavior::HourlyEvent','ImprovedGarrisons.SaveSystem.GarrisonBehavior::DailyEvent'],[('ImprovedGarrisons','ImprovedGarrisons.dll'),('CoopModPatch','CoopModPatch.dll'),('CoopModPatch','Adapters/Adapter.ImprovedGarrisons.dll')]),
    contract('mlw.editing','MyLittleWarband','Troop editing and snapshots','CoopModPatch',['MyLittleWarband.CustomUnitsBehavior::UpdateSelectedUnitEquipment'],[('MyLittleWarband','MyLittleWarband.dll'),('CoopModPatch','CoopModPatch.dll'),('CoopModPatch','Adapters/Adapter.MyLittleWarband.dll')]),
    contract('clans-resource-adder.daily','Europe1100','Daily AI clan resources','ModderLords',['ClansResourceAdder.ResourcesAdderEvents::AddResources','ClansResourceAdder.ResourcesAdderEvents::is_ai_clan'],[('Europe1100','ClansResourceAdder.dll'),('CoopNightly','GameInterface.dll'),('CoopNightly','Common.dll'),('CoopNightly','Coop.Core.dll')],adapter='clans-resource-adder.v1')]
destination=Path(__file__).resolve().parents[2]/'src'/'ModderLords.Analysis'/'provider-contracts.json'
destination.write_text(json.dumps(contracts,indent=2)+'\n',encoding='utf-8')
