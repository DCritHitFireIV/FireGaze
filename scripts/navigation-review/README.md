# 人工核实后收录中文导航

本工具只生成候选目录，不联网、不创建Issue/PR、不推送或安装插件。玩家投稿不是核实结论。

1. 复制 `review-template.json`，核对精确插件来源、项目、版本、API与国服身份；确认该版本实际消费中文资源。
2. 填写维护者、安装/启用说明、公开证据与入口。完整与部分使用 `full` / `partial`；未知不能作为已核实推荐。替代版记录独立名字和兼容性，不改变原插件类别。
3. 人工确认身份、语言消费、覆盖、兼容性及公开数据后，填写审核者/日期并设置相应责任声明；仅此时将decision改为verified。不能照抄投稿中的verified标记。
4. 以最新 `Resources/chinese-navigation-index.json` 的SHA256和revision填写基线，设置新revision，然后执行：

```powershell
python -B scripts/navigation-review/review_index.py --base Resources/chinese-navigation-index.json --reviews reviewed-batch.json --output new-candidate.json
```

工具只创建不存在的输出，拒绝旧基线、重复entryId/审核及用旧entryId换另一身份；保留无关条目。撤销推荐用withdrawn记录，保留历史，不删除用户文件。隐私拦截不是万能检测，人工公开数据确认仍必需。

提交已人工核实的批次到 `docs/chinese-navigation/reviews/<revision>.json` 后，可手动运行 `chinese-navigation-review.yml` 生成候选artifact；该工作流只有contents:read，不自动提交、合并、安装或发送通知。维护者检查差异并走正常PR把候选加入 `Resources/chinese-navigation-index.json`，引用来源Issue和审核记录；主分支变化后重新核对基线。

客户端启动只读内嵌目录和本地 `uitrans/chinese-navigation-reviewed.json`。详情中的“刷新中文导航索引”才请求固定官方仓库raw路径；最多20秒、2MB，不接受跳转，校验和保存成功后才显示更新成功。HTTP404、无效内容、取消、输入目录已变化或保存失败均保留原目录。不会周期扫描、下载插件、变更分类或把未审核投稿推荐给玩家。

当前内嵌目录为空，这是如实交付的可用通道，不代表189项已有中文方案。GitHub索引上线与客户端正常重载单独记账，不能因内嵌接线就宣称远程发布完成。
