#include "service-startup-trace.h"
#include "service-session.h"
#include <cassert>
#include <cstring>
#include <string>

using namespace ClassicDeskService;

struct ReadableTrace {
    std::string text;
    std::size_t next=0;
    static void Append(const StartupTrace& trace,void* context) {
        auto& sink=*static_cast<ReadableTrace*>(context);
        while(sink.next<trace.Count()) {
            char line[512]{};std::size_t length=0;
            assert(trace.SerializeEntry(sink.next++,line,sizeof(line),length));
            sink.text.append(line,length);
        }
    }
};
struct FailingEndApi {
    ServiceRuntimeMetadata metadata;
    std::uint32_t cleanupError=0;
    unsigned endCalls=0;
    void* Start(){metadata.MarkSessionStarted(true);return this;}
    bool Scan(void*){return true;}
    void End(void*){++endCalls;metadata.MarkSessionEnded(false);cleanupError=31;}
};

int main() {
    ServiceRuntimeMetadata metadata;
    assert(metadata.Mode()==StartupMode::Unknown);
    assert(!metadata.EngineStarted());
    metadata.SetMode(StartupMode::Normal);
    assert(metadata.Mode()==StartupMode::Normal);
    assert(metadata.SessionId()==0 && metadata.ExplorerPid()==0);
    metadata.MarkSessionStarted(false);
    assert(!metadata.EngineStarted());
    metadata.MarkSessionStarted(true);
    assert(metadata.EngineStarted());
    metadata.MarkSessionEnded(false);
    assert(metadata.EngineStarted());
    metadata.MarkSessionEnded(true);
    assert(!metadata.EngineStarted());

    metadata.SetMode(StartupMode::SessionOnly,7,4512);
    assert(metadata.Mode()==StartupMode::SessionOnly);
    assert(metadata.SessionId()==7 && metadata.ExplorerPid()==4512);
    assert(!metadata.EngineStarted());
    metadata.MarkSessionStarted(true);
    assert(metadata.EngineStarted());
    metadata.MarkSessionEnded(true);
    assert(!metadata.EngineStarted());
    metadata.Reset();
    assert(metadata.Mode()==StartupMode::Unknown && !metadata.EngineStarted());
    assert(metadata.SessionId()==0 && metadata.ExplorerPid()==0);

    StartupTrace completed(1000);
    completed.SetMode(StartupMode::Normal);
    assert(completed.Record(StartupStage::Verify,StartupOutcome::Succeeded,1100,100));
    assert(completed.Record(StartupStage::Compatibility,StartupOutcome::Succeeded,1120,20));
    assert(completed.Record(StartupStage::EngineLoad,StartupOutcome::Succeeded,1200,80));
    assert(completed.Record(StartupStage::GlobalHookSessionStart,StartupOutcome::Succeeded,1600,400));
    assert(completed.Record(StartupStage::FirstScan,StartupOutcome::Succeeded,1850,250));
    assert(completed.Record(StartupStage::Running,StartupOutcome::Succeeded,1851));
    assert(completed.Record(StartupStage::Running,StartupOutcome::Stopped,12851,11000));
    assert(completed.Record(StartupStage::End,StartupOutcome::Succeeded,12855,4));
    assert(!completed.HasFailure());
    assert(completed.Count()==8);
    const auto* events=completed.Entries();
    const StartupStage expected[]={StartupStage::Verify,StartupStage::Compatibility,
        StartupStage::EngineLoad,StartupStage::GlobalHookSessionStart,StartupStage::FirstScan,
        StartupStage::Running,StartupStage::Running,StartupStage::End};
    for(std::size_t i=0;i<completed.Count();i++)assert(events[i].stage==expected[i]);
    assert(events[0].durationMs==100 && events[3].durationMs==400 && events[4].durationMs==250);
    assert(events[6].outcome==StartupOutcome::Stopped && events[6].durationMs==11000);

    StartupTrace failedFirstScan(2000);
    failedFirstScan.SetMode(StartupMode::SessionOnly);
    assert(failedFirstScan.Record(StartupStage::Verify,StartupOutcome::Succeeded,2050,50));
    assert(failedFirstScan.Record(StartupStage::Compatibility,StartupOutcome::Skipped,2050));
    assert(failedFirstScan.Record(StartupStage::EngineLoad,StartupOutcome::Succeeded,2100,50));
    assert(failedFirstScan.Record(StartupStage::GlobalHookSessionStart,StartupOutcome::Succeeded,2300,200));
    assert(failedFirstScan.Record(StartupStage::FirstScan,StartupOutcome::Failed,2450,150,31));
    assert(failedFirstScan.Record(StartupStage::Failure,StartupOutcome::Failed,2450,0,31));
    assert(failedFirstScan.Record(StartupStage::End,StartupOutcome::Succeeded,2452,2));
    assert(failedFirstScan.HasFailure());
    assert(failedFirstScan.Count()==7);
    assert(failedFirstScan.Entries()[4].stage==StartupStage::FirstScan);
    assert(failedFirstScan.Entries()[5].stage==StartupStage::Failure);
    assert(failedFirstScan.Entries()[6].stage==StartupStage::End);

    StartupTrace failedEnd(3000);
    failedEnd.SetMode(StartupMode::Normal);
    assert(failedEnd.Record(StartupStage::Running,StartupOutcome::Succeeded,3100));
    assert(failedEnd.Record(StartupStage::Running,StartupOutcome::Stopped,3200,100));
    assert(failedEnd.Record(StartupStage::End,StartupOutcome::Failed,3250,50,31));
    assert(failedEnd.Record(StartupStage::Failure,StartupOutcome::Failed,3250,0,31));
    assert(failedEnd.HasFailure());
    assert(failedEnd.Entries()[2].stage==StartupStage::End);
    assert(failedEnd.Entries()[2].outcome==StartupOutcome::Failed);
    assert(failedEnd.Entries()[3].stage==StartupStage::Failure);

    char output[StartupTrace::MaximumBytes]{};
    std::size_t length=0;
    assert(failedFirstScan.Serialize(4512,2500,output,sizeof(output),length));
    assert(length>0 && length<=StartupTrace::MaximumBytes);
    assert(output[length]=='\0');
    assert(std::strstr(output,"pid=4512 mode=session-only")!=nullptr);
    assert(std::strstr(output,"started_boot_elapsed_ms=2000")!=nullptr);
    assert(std::strstr(output,"stage=engineLoad phase=event result=ok")!=nullptr);
    assert(std::strstr(output,"stage=GlobalHookSessionStart phase=event result=ok boot_elapsed_ms=2300 duration_ms=200 error=0")!=nullptr);
    assert(std::strstr(output,"stage=firstScan phase=event result=failed boot_elapsed_ms=2450 duration_ms=150 error=31")!=nullptr);
    assert(std::strstr(output,"stage=failure phase=event result=failed")!=nullptr);
    assert(std::strstr(output,"stage=End phase=event result=ok")!=nullptr);
    char shortOutput[8]{};
    assert(!failedFirstScan.Serialize(4512,2500,shortOutput,sizeof(shortOutput),length));

    StartupTrace bounded;
    for(std::size_t i=0;i<StartupTrace::MaximumEntries;i++)
        assert(bounded.Record(StartupStage::Failure,StartupOutcome::Failed,UINT64_MAX,UINT64_MAX,UINT32_MAX));
    assert(!bounded.Record(StartupStage::End,StartupOutcome::Succeeded,0));
    assert(bounded.Count()==StartupTrace::MaximumEntries);
    assert(bounded.Serialize(UINT32_MAX,UINT64_MAX,output,sizeof(output),length));
    assert(length<=StartupTrace::MaximumBytes && output[length]=='\0');

    // The observer must see a begin record before an API returns (or never
    // returns), without depending on trace/service destruction.
    StartupTrace live(4000);ReadableTrace sink;
    live.SetSink(ReadableTrace::Append,&sink);
    assert(live.Begin(StartupStage::GlobalHookSessionStart,4100));
    assert(sink.text.find("phase=begin result=pending")!=std::string::npos);
    assert(sink.text.find("phase=end")==std::string::npos);
    assert(live.Complete(StartupStage::GlobalHookSessionStart,StartupOutcome::Succeeded,4500));
    assert(sink.text.find("phase=end result=ok boot_elapsed_ms=4500 duration_ms=400")!=std::string::npos);
    const auto observed=sink.text;
    live.SetSink(nullptr);
    assert(live.Record(StartupStage::Running,StartupOutcome::Succeeded,4501));
    assert(sink.text==observed);

    StartupTrace paired;
    for(auto stage:{StartupStage::Verify,StartupStage::Compatibility,StartupStage::EngineLoad,
        StartupStage::GlobalHookSessionStart,StartupStage::FirstScan,StartupStage::Running,StartupStage::End}) {
        assert(paired.Begin(stage,100));assert(paired.Complete(stage,StartupOutcome::Succeeded,101));
    }
    assert(paired.Record(StartupStage::Failure,StartupOutcome::Failed,102,0,31));
    assert(paired.Count()<StartupTrace::MaximumEntries);
    assert(paired.Serialize(1,102,output,sizeof(output),length));

    FailingEndApi api;
    {Session session(api);assert(session.Start());session.Stop();}
    assert(api.endCalls==1 && api.metadata.EngineStarted());
    assert(PreservePrimaryError(0,api.cleanupError)==31);
    assert(PreservePrimaryError(123,api.cleanupError)==123);
}
