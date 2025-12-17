# BugFree.Configuration

![.NET](https://img.shields.io/badge/.NET-net8.0%20%7C%20net10.0-blue)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)
![License](https://img.shields.io/badge/License-MIT-green)

BugFree.Configuration 是一个强类型配置读写库，提供统一的 `ConfigProvider` 抽象与 `Config<T>.Current` 编程模型，支持 JSON/XML/INI/YAML，支持可选加密（AES-GCM）、原子写入保存与文件热重载。

本包目标框架：`net8.0`、`net10.0`。

## 能力概览（基于当前代码实现）

- 多格式：`ConfigProviderType.Json/Xml/Ini/Yaml`
- 强类型：配置类继承 `Config<T>` 后，通过 `T.Current` 访问
- 热重载：文件变化后自动重新加载并替换 `Current`
- 可选加密：`ConfigAttribute.IsEncrypted` + `Secret`（AES-GCM，依赖 BugFree.Security）
- 原子写入：保存时写临时文件后 `Move` 覆盖，尽量减少“半文件”
- 注释注入：未加密时，会将带 `[Description]` 的属性说明写入文件头
  - JSON 读取允许注释与尾随逗号（`ReadCommentHandling = Skip`，`AllowTrailingCommas = true`）

## 安装

```powershell
Install-Package BugFree.Configuration
```

或使用 PackageReference：

```xml
<PackageReference Include="BugFree.Configuration" Version="<YOUR_VERSION>" />
```

## 快速开始（推荐：继承 Config<T>）

### 1) 定义配置类

`Config<T>` 通过类型上的 `ConfigAttribute` 决定：提供者类型、文件名、目录、是否加密、热重载方式。

```csharp
using BugFree.Configuration;
using System.ComponentModel;

[Config("AppSettings", ConfigProviderType.Json, "./config")]
public class AppConfig : Config<AppConfig>
{
    [Description("应用程序名称")]
    public String AppName { get; set; } = "MyApp";

    [Description("服务器端口")]
    public Int32 Port { get; set; } = 8080;

    [Description("是否启用调试模式")]
    public Boolean Debug { get; set; }

    protected override void OnLoaded()
    {
        if (IsNew)
        {
            // 首次创建/文件为空时，会返回默认实例，且 IsNew 为 true
            // 你可以在这里补充默认值，然后 Save()
        }
    }
}
```

### 2) 读取、修改、保存

```csharp
var cfg = AppConfig.Current;

Console.WriteLine(cfg.AppName);
cfg.Port = 9090;
cfg.Save();
```

说明：

- `Current` 首次访问会读取文件，不存在则返回新实例并自动保存一次默认文件（便于用户编辑）。
- `Save()` 内部会在保存前后进行“自触发抑制”标记，避免保存引发热重载回调。

## 文件命名与路径规则（ConfigAttribute.GetFullPath）

默认行为：

- `Path` 默认为 `./config`（可不填，走默认值）
- `Name` 默认是 `Config`（若不填）
- 文件名规则：
  - `Name` 不带扩展名时：自动追加 `.{Provider}`，例如 `AppSettings.json`
  - `Name` 已带扩展名时：直接使用该文件名，例如 `garnet.conf`

## 热重载（HotReloader）

`Config<T>.Current` 在首次加载后会按 `ConfigAttribute.Reloader` 启动热重载器：

- `HotReloaderType.FileWatcher`：文件系统监视器
- `HotReloaderType.Timer`：定时轮询

当文件被外部修改时，会重新读取并替换 `Current` 实例；调用方之后再次访问 `T.Current` 将拿到新实例。

### Save 自触发抑制

热重载器内部有一个短暂的抑制窗口，`Save()` 会在保存前后各调用一次 `MarkFileChanged()`：

- 保存前：进入抑制窗口，避免写临时文件/覆盖造成的重复触发
- 保存后：更新写入时间基线

## 可选加密（AES-GCM）

通过 `ConfigAttribute` 开启：

```csharp
using BugFree.Configuration;
using System.ComponentModel;

[Config("SecureConfig", ConfigProviderType.Json, "./config", isencrypted: true, secret: "0123456789ABCDEF0123456789ABCDEF")]
public class SecureConfig : Config<SecureConfig>
{
    [Description("API密钥")]
    public String ApiKey { get; set; } = "";
}
```

注意：

- 加密启用后，为避免泄露结构信息，不会注入 `[Description]` 注释头。
- `Secret` 需要稳定且足够强；示例仅为演示。

## 无需继承 Config<T>：ConfigExtensions（现有实现的正确用法与限制）

项目当前并没有 `ConfigStore` 类型（全仓无该符号），无需继承的入口是 `ConfigExtensions` 的扩展方法：

- `Load<T>(this T model, ConfigAttribute attribute)`
- `Save<T>(this T model, String? path = null)`

### 适用场景

- 目标类型不方便继承基类（第三方 Options/DTO），但仍希望复用本库的 Provider、路径规则、加密与写入逻辑。

### 基本示例

```csharp
using BugFree.Configuration;
using System.ComponentModel;

public class ThirdPartyOptions
{
    [Description("端口")]
    public Int32 Port { get; set; } = 6379;
}

var attr = new ConfigAttribute("thirdparty.conf", ConfigProviderType.Json, "./")
{
    // 可选：文件监视/轮询
    Reloader = HotReloaderType.Timer
};

// 第一次 Load 会缓存 ConfigAttribute，并在文件不存在时写入默认文件
var options = new ThirdPartyOptions().Load(attr);

options.Port = 6380;
options.Save("./thirdparty.conf");
```

### 重要限制（必须看）

1) `Save()` 依赖缓存的 `ConfigAttribute`

- `ConfigExtensions.Save()` 会从内部缓存中取 `ConfigAttribute`。
- 因此必须先对该类型调用一次 `Load()`，再 `Save()`。

2) “热重载”不会自动更新你手里的对象引用

`ConfigExtensions.Load()` 虽然会启动热重载器，但回调内部只是重新 `provider.Load<T>(attribute)`，并没有把新对象回写给调用方持有的 `options` 变量。

这意味着：

- 外部修改文件后，本库会“读到新对象”，但你原来持有的 `options` 不会自动变成新值。
- 如果你需要“外部变更后自动替换实例”的语义，请使用 `Config<T>.Current`；
- 如果你坚持使用 `ConfigExtensions`，则需要你自己在回调/业务层重新调用 `Load()` 并替换引用（当前实现未提供事件/回调参数）。

3) `path` 参数的语义

- `Save(path)` 会把内容保存到指定 `path`（实现层面由 `ConfigProvider.Save(model, attr, path)` 处理）。
- 但热重载器监视的文件路径来自 `attribute.GetFullPath()`，如果你把 `Save` 指向另一个路径，热重载器不会跟随变更（路径不一致时的行为需要调用方自行保证一致）。

## 许可证

MIT，见 [BugFree.Configuration/LICENSE](BugFree.Configuration/LICENSE)。
