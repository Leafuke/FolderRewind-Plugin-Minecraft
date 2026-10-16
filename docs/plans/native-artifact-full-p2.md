# P2 Full格式

基线 e7d780e；分支 codex/native-artifact-full。仅开发候选，不替换安装或Host内置包。
新增既有Transformer/Materializer capability，API仍为3.9。
格式 com.folderrewind.minerewind/minecraft-world-full v1；Transformer minecraft-world-full；
strategy minecraft-world-materializer。仅Java、Full、Complete，自包含，不依赖primary。

复用P1无损codec，持久格式剔除probe计时/中间region副本。输入经受控handles读取，
在限额temporary workspace完成seek适配，输出只进入Host staging。
没有生产恢复目标写权限或密码。未知region原样保存且报告覆盖诊断，字节恢复必须Exact。
