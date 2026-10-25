namespace ClassicDesk;

public static class ShellServiceReview
{
    public static ShellNativeReview Review(this ShellServiceObservation service) => service.StartupProtection == "cold-diagnostic-expired" ? new(
        "开机验证已过期，增强没有加载",
        "此前的限时验证在重启前已过期，服务未加载增强；这不是本次增强崩溃。请使用新版的下一次启动验证，准备后可按正常工作安排重启。完成实际验收后再启用持续自动加载。") : service.StartupProtection is not null ? new(
        "开机增强已被兼容保护拦截",
        service.StartupProtection == "compatibility-changed" ? "Windows 外壳、增强组件或方案已改变，原兼容验收已失效。为保留原生任务栏，本次没有加载增强。请先检查当前环境，再进行增强验证。" :
        service.StartupProtection == "compatibility-unavailable" ? "无法完整读取系统组件与方案信息，本次没有加载增强。请查看诊断结果；程序不会把读取失败当作兼容。" :
        "当前系统与增强方案尚未完成真实验收，本次没有加载增强。原生任务栏正常不等于增强兼容；方案和恢复记录已保留。") : service.UpdatePending && service.StartMode != 4 ? new(
        "开机更新已准备，等待兼容验收",
        "当前桌面仍使用原配置。已保存的方案须通过当前系统的增强兼容验收后才能自动加载；没有停止服务或重新加载资源管理器。") : new(
        service.State == 4 && service.StartMode == 4 ? "本次增强正在运行，开机自动加载已停用" : service.State == 4 ? "增强正在运行，下次启动已安排" : service.StartMode == 2 ? "已安排下次开机加载" : "开机组件已登记，自动加载已停用",
        (service.State == 4 && service.StartMode == 4 ? "当前会话恢复不会开启自启；下次重启后不会自动加载。需要完成开机验证并保存持续自动加载设置。" : service.State == 4 ? "系统服务已运行，设置窗口可以退出。模块加载和桌面首次显示效果仍需分别验收。" :
         service.StartMode == 2 ? "当前不会重启资源管理器；下次开机由系统服务加载已应用的整套规则。" : "保留安装文件及恢复记录，当前不会自动启动。") +
        "\n此安装使用整机统一布局。旧便携引擎的启用、登录补加载和恢复入口已锁定，避免重复加载；在设置页点击系统应用检查，可对比并更新开机方案，或停用下次开机增强。保存草稿与更新开机方案是两个独立操作。");
}
