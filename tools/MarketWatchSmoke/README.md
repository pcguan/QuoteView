# 全市场行情桌面逻辑回归

在装有 .NET 8 SDK 的 Windows 上运行：

```powershell
dotnet run --project tools/MarketWatchSmoke -c Release
```

链接正式 ViewModel、列布局及表头样式代码，以内存偏好和受控 HTTP 响应验证：列宽/顺序/显隐保存、跨视图同步、恢复默认、表头对齐，以及多个视图的共享刷新、最后视图关闭后的取消、重新打开后的冷却和快照保留。不启动窗口、不调用真实接口、不读写用户配置。

真实接口验证使用 `dotnet run --project tools/Smoke -- --market`。
