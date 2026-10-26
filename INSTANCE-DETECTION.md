# 本地实例检测

扫描器只枚举 `instances` 的直接子文件夹。不会下载文件、启动游戏或修改整合包。

- 标准目录：实例名使用文件夹名，游戏目录优先为 `.minecraft`；也支持实例目录本身包含 `versions`。读取 `versions/<目录名>/<目录名>.json`。
- 游戏版本：优先读取合并清单中的 `game` 补丁或 `clientVersion`，其次解析 `inheritsFrom`，旧版 Forge 可读取 Maven 坐标中的 Minecraft 版本。原版使用清单 ID。不会用资产索引或文件夹名猜测版本。
- Prism：读取 `instance.cfg` 的 `[General] name`，名称缺失时使用文件夹名；版本读取 `mmc-pack.json` 中的 `net.minecraft` 组件。支持 `.minecraft` 和 `minecraft` 游戏目录，无需 `versions`。
- 识别出的文件夹不等于完整可启动实例；损坏或缺失清单会显示提示。
- `VersionIds` 保留用于兼容旧接口，新增 `Versions` 提供游戏本体版本、加载器和元数据路径。Prism 没有标准版本配置 ID，此字段使用 Minecraft 组件版本，启动模块必须按 `Format` 区分处理。

在界面“本地实例”页点击“刷新实例”。上方显示实际扫描目录，选择条目查看实例路径、游戏目录及提示。

Debug 构建中，App 会向上寻找 `EML.slnx` 并使用解决方案旁的 `instances`，方便开发测试。Release 构建始终使用 EXE 所在目录。扫描器本身默认使用 EXE 目录，也可由构造函数显式传入根目录。

发布时手动将整合包放到 EXE 旁的 `instances`，不会自动复制或打包测试整合包。

运行测试：`dotnet test EML.slnx`。测试使用独立临时数据，不依赖个人目录或真实整合包。
