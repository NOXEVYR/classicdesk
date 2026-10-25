#pragma once

#include <cstddef>
#include <cstdint>
#include <cstdio>

namespace ClassicDeskService {

enum class StartupMode : std::uint8_t { Unknown, Normal, SessionOnly, ColdDiagnostic };
enum class StartupStage : std::uint8_t {
    Verify,
    Compatibility,
    EngineLoad,
    GlobalHookSessionStart,
    FirstScan,
    Running,
    Failure,
    End
};
enum class StartupOutcome : std::uint8_t { Succeeded, Failed, Skipped, Stopped };
enum class StartupRecordKind : std::uint8_t { Instant, Begin, Complete };

struct StartupTraceEntry {
    StartupStage stage{};
    StartupRecordKind kind=StartupRecordKind::Instant;
    StartupOutcome outcome{};
    std::uint64_t bootElapsedMs=0;
    std::uint64_t durationMs=0;
    std::uint32_t error=0;
};

// This metadata describes what the service actually did during this process.
// A session remains started until GlobalHookSessionEnd confirms that it ended.
class ServiceRuntimeMetadata {
    StartupMode mode_=StartupMode::Unknown;
    bool engineStarted_=false;
    std::uint32_t sessionId_=0;
    std::uint32_t explorerPid_=0;
public:
    void Reset() {
        mode_=StartupMode::Unknown;
        engineStarted_=false;
        sessionId_=0;
        explorerPid_=0;
    }
    void SetMode(StartupMode mode,std::uint32_t sessionId=0,std::uint32_t explorerPid=0) {
        mode_=mode;
        sessionId_=mode==StartupMode::SessionOnly?sessionId:0;
        explorerPid_=mode==StartupMode::SessionOnly?explorerPid:0;
    }
    void MarkSessionStarted(bool returnedHandle) { engineStarted_=returnedHandle; }
    void MarkSessionEnded(bool endedSuccessfully) { if(endedSuccessfully)engineStarted_=false; }
    StartupMode Mode() const { return mode_; }
    bool EngineStarted() const { return engineStarted_; }
    std::uint32_t SessionId() const { return sessionId_; }
    std::uint32_t ExplorerPid() const { return explorerPid_; }
};

class StartupTrace {
public:
    static constexpr std::size_t MaximumEntries=24;
    static constexpr std::size_t MaximumBytes=32*1024;
private:
    using Sink=void(*)(const StartupTrace&,void*);
    Sink sink_=nullptr;
    void* sinkContext_=nullptr;
    StartupTraceEntry entries_[MaximumEntries]{};
    std::size_t count_=0;
    StartupMode mode_=StartupMode::Unknown;
    std::uint64_t startedAtBootElapsedMs_=0;

    static const char* ModeName(StartupMode value) {
        switch(value) {
            case StartupMode::Normal:return "normal";
            case StartupMode::SessionOnly:return "session-only";
            case StartupMode::ColdDiagnostic:return "cold-diagnostic";
            default:return "unknown";
        }
    }
    static const char* StageName(StartupStage value) {
        switch(value) {
            case StartupStage::Verify:return "Verify";
            case StartupStage::Compatibility:return "compatibility";
            case StartupStage::EngineLoad:return "engineLoad";
            case StartupStage::GlobalHookSessionStart:return "GlobalHookSessionStart";
            case StartupStage::FirstScan:return "firstScan";
            case StartupStage::Running:return "running";
            case StartupStage::Failure:return "failure";
            case StartupStage::End:return "End";
        }
        return "unknown";
    }
    static const char* OutcomeName(StartupOutcome value) {
        switch(value) {
            case StartupOutcome::Succeeded:return "ok";
            case StartupOutcome::Failed:return "failed";
            case StartupOutcome::Skipped:return "skipped";
            case StartupOutcome::Stopped:return "stopped";
        }
        return "unknown";
    }
    static const char* KindName(StartupRecordKind value) {
        switch(value) {
            case StartupRecordKind::Instant:return "event";
            case StartupRecordKind::Begin:return "begin";
            case StartupRecordKind::Complete:return "end";
        }
        return "event";
    }
    bool OpenStageStart(StartupStage stage,std::uint64_t& startedAt) const {
        for(std::size_t i=count_;i>0;i--) {
            const auto& entry=entries_[i-1];
            if(entry.stage!=stage)continue;
            if(entry.kind==StartupRecordKind::Complete)return false;
            if(entry.kind==StartupRecordKind::Begin) {startedAt=entry.bootElapsedMs;return true;}
        }
        return false;
    }
public:
    explicit StartupTrace(std::uint64_t startedAtBootElapsedMs=0):startedAtBootElapsedMs_(startedAtBootElapsedMs) {}
    void SetSink(Sink sink,void* context=nullptr) { sink_=sink;sinkContext_=context; }
    void SetMode(StartupMode mode) { mode_=mode; }
    StartupMode Mode() const { return mode_; }
    std::uint64_t StartedAtBootElapsedMs() const { return startedAtBootElapsedMs_; }
    std::size_t Count() const { return count_; }
    const StartupTraceEntry* Entries() const { return entries_; }
    const StartupTraceEntry* Entry(std::size_t index) const { return index<count_?&entries_[index]:nullptr; }
    bool HasStage(StartupStage stage) const {
        for(std::size_t i=0;i<count_;i++)if(entries_[i].stage==stage)return true;
        return false;
    }
    bool HasFailure() const { return HasStage(StartupStage::Failure); }
    bool Record(StartupStage stage,StartupOutcome outcome,std::uint64_t bootElapsedMs,
            std::uint64_t durationMs=0,std::uint32_t error=0) {
        if(count_>=MaximumEntries)return false;
        entries_[count_++]={stage,StartupRecordKind::Instant,outcome,bootElapsedMs,durationMs,error};
        if(sink_)sink_(*this,sinkContext_);
        return true;
    }
    bool Begin(StartupStage stage,std::uint64_t bootElapsedMs) {
        if(count_>=MaximumEntries)return false;
        entries_[count_++]={stage,StartupRecordKind::Begin,StartupOutcome::Succeeded,bootElapsedMs,0,0};
        if(sink_)sink_(*this,sinkContext_);
        return true;
    }
    bool Complete(StartupStage stage,StartupOutcome outcome,std::uint64_t bootElapsedMs,std::uint32_t error=0) {
        if(count_>=MaximumEntries)return false;
        std::uint64_t began=0;
        const bool hasBegin=OpenStageStart(stage,began);
        entries_[count_++]={stage,StartupRecordKind::Complete,outcome,bootElapsedMs,
            hasBegin && bootElapsedMs>=began?bootElapsedMs-began:0,error};
        if(sink_)sink_(*this,sinkContext_);
        return true;
    }
    bool SerializeHeader(std::uint32_t processId,char* output,std::size_t capacity,std::size_t& length) const {
        length=0;
        if(!output || capacity==0 || capacity>MaximumBytes)return false;
        const int written=std::snprintf(output,capacity,
            "run pid=%lu mode=%s started_boot_elapsed_ms=%llu\n",
            static_cast<unsigned long>(processId),ModeName(mode_),
            static_cast<unsigned long long>(startedAtBootElapsedMs_));
        if(written<0 || static_cast<std::size_t>(written)>=capacity)return false;
        length=static_cast<std::size_t>(written);
        return true;
    }
    bool SerializeEntry(std::size_t index,char* output,std::size_t capacity,std::size_t& length) const {
        length=0;
        if(!output || capacity==0 || capacity>MaximumBytes || index>=count_)return false;
        const auto& entry=entries_[index];
        const int written=std::snprintf(output,capacity,
            "stage=%s phase=%s result=%s boot_elapsed_ms=%llu duration_ms=%llu error=%lu\n",
            StageName(entry.stage),KindName(entry.kind),entry.kind==StartupRecordKind::Begin?"pending":OutcomeName(entry.outcome),
            static_cast<unsigned long long>(entry.bootElapsedMs),
            static_cast<unsigned long long>(entry.durationMs),static_cast<unsigned long>(entry.error));
        if(written<0 || static_cast<std::size_t>(written)>=capacity)return false;
        length=static_cast<std::size_t>(written);
        return true;
    }
    bool Serialize(std::uint32_t processId,std::uint64_t currentBootElapsedMs,
            char* output,std::size_t capacity,std::size_t& length) const {
        length=0;
        if(!output || capacity==0 || capacity>MaximumBytes)return false;
        output[0]='\0';
        const int header=std::snprintf(output,capacity,
            "run pid=%lu mode=%s started_boot_elapsed_ms=%llu current_boot_elapsed_ms=%llu\n",
            static_cast<unsigned long>(processId),ModeName(mode_),
            static_cast<unsigned long long>(startedAtBootElapsedMs_),
            static_cast<unsigned long long>(currentBootElapsedMs));
        if(header<0 || static_cast<std::size_t>(header)>=capacity)return false;
        length=static_cast<std::size_t>(header);
        for(std::size_t i=0;i<count_;i++) {
            std::size_t written=0;
            if(!SerializeEntry(i,output+length,capacity-length,written)) {
                output[capacity-1]='\0';
                return false;
            }
            length+=written;
        }
        return length<=MaximumBytes;
    }
};

inline std::uint32_t PreservePrimaryError(std::uint32_t primary,std::uint32_t later) {
    return primary?primary:later;
}

} // namespace ClassicDeskService
