#pragma once
namespace ClassicDeskService {
class CrashWindow {
    unsigned long long times[3]{};
    unsigned count=0;
public:
    bool Add(unsigned long long now) {
        times[0]=times[1];times[1]=times[2];times[2]=now;
        if(count<3)++count;
        return count==3 && now-times[0]<=60000;
    }
};
// Kept separate from SCM/Windows so failure and shutdown ordering can be tested
// without starting a service or loading the injection engine on the developer PC.
template<class Api> class Session {
    Api& api;
    void* session=nullptr;
public:
    explicit Session(Api& value):api(value) {}
    bool Start() {
        if(session) return false;
        session=api.Start();
        if(!session) return false;
        if(!api.Scan(session)) {Stop();return false;}
        return true;
    }
    bool Scan() {return session && api.Scan(session);}
    void Stop() {if(session) {auto previous=session;session=nullptr;api.End(previous);}}
    ~Session() {Stop();}
};
}
