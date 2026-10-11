# SmoothNavDriver の単体試験

野外の移動の角を曲線に整える部品 `ICE/Scheduler/Tasks/SmoothNavDriver.cs` を、ゲームに触れずに試す。
vnavmesh の代わりに偽の部品(平らな面・途切れる面・結果を後から返す経路探索)を使う。

```
dotnet run -c Release --project tools/SmoothNavDriverTests
```

最後に「単体試験 N/N 合格」と出れば成功(失敗があれば終了コード 1)。
