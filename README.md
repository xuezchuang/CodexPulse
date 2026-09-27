# Codex Pulse

一个 Windows 10/11 桌面小组件，把以下状态合并到同一个圆角面板：

- 当前 Codex 模型的正确剩余额度和精确重置时间
- 实时上传、下载速度
- 全系统 CPU 使用率
- CPU Package 温度（由本机 Armoury Crate 硬件监控组件读取；传感器不可用时显示 `--°C`）
- 物理内存使用率

## 使用

1. 双击 `dist\CodexPulse.exe`。
2. 拖动小组件可改变位置；双击小组件恢复到任务栏右上方。
3. 右键小组件或托盘图标可刷新、隐藏、恢复位置、设置开机启动或退出。

Codex 用量每 5 分钟通过本机 `codex app-server` 的标准输入输出查询一次。程序
不会扫描会话日志，也不会读取或保存登录凭证；凭证和上游 HTTPS 请求均由 Codex
自身处理。界面只显示普通 Codex 额度，忽略独立的 Spark 额度桶。

额度查询优先使用桌面版 `bin` 版本子目录中最新的 `codex.exe`，其次查找 PATH 和
npm 安装的原生程序，最后兼容旧版桌面安装路径；不通过 `cmd`/Node 启动器启动。
查询进程在本机 LocalAppData 目录运行，并通过命令行仅为该进程关闭插件、Apps 和
Code Mode host，避免额度刷新触发插件市场 Git 同步，不修改用户的 Codex 配置。
查询完成或超时后，关闭标准输入并等待正常退出；超过 5 秒才终止该查询进程，
不再强杀整个进程树。

系统状态每秒采样一次。网速默认合计当前在线的物理以太网和 Wi-Fi 网卡，排除
回环、隧道、VPN 和常见虚拟网卡。

## 构建

```powershell
dotnet build .\CodexPulse.csproj -c Release
dotnet run --project .\tests\CodexPulse.SmokeTests\CodexPulse.SmokeTests.csproj -c Release
dotnet publish .\CodexPulse.csproj -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -o .\dist
```
