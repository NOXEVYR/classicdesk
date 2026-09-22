namespace ClassicDesk;

public static class ShellServiceReview
{
    public static ShellNativeReview Review(this ShellServiceObservation service) => service.UpdatePending ? new(
        "开机更新已准备，等待重启生效",
        "当前桌面仍使用原配置。下次重启 Windows 后自动加载已保存的方案及组件，无需打开 ClassicDesk；没有停止服务或热重载资源管理器。") : new(
        service.State == 4 ? "开机组件正在运行" : service.StartMode == 2 ? "已安排下次开机加载" : "开机组件已登记，自动加载已停用",
        (service.State == 4 ? "系统服务已运行，设置窗口可以退出。模块加载和桌面首次显示效果仍需分别验收。" :
         service.StartMode == 2 ? "当前不会重启资源管理器；下次开机由系统服务加载已应用的整套规则。" : "保留安装文件及恢复记录，当前不会自动启动。") +
        "\n此安装使用整机统一布局。旧便携引擎的启用、登录补加载和恢复入口已锁定，避免重复加载；在设置页点击系统应用检查，可对比并更新开机方案，或停用下次开机增强。保存草稿与更新开机方案是两个独立操作。");
}
