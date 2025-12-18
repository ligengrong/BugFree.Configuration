# BugFree.Configuration

![.NET](https://img.shields.io/badge/.NET-net8.0%20%7C%20net10.0-blue)
![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux%20%7C%20macOS-lightgrey)
![License](https://img.shields.io/badge/License-MIT-green)

BugFree.Configuration 是一个强类型配置读写库，提供统一的 `ConfigProvider` 抽象与 `Config<T>.Current` 编程模型，支持 JSON/XML/INI/YAML，支持可选加密（AES-GCM）、原子写入保存与文件热重载。

本包目标框架：`net8.0`、`net10.0`。

## 能力概览（基于当前代码实现）

- 多格式：`ConfigProviderType.Json/Xml/Ini/Yaml`
- 强类型：配置类继承 `Config<T>` 后，通过 `T.Current` 访问
- 无需继承：使用 `Config.Instance.Load/Current/Save` 绑定第三方 Options/DTO
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

## 无需继承 Config<T>：Config（现有实现的正确用法与限制）

当目标类型不方便继承 `Config<T>`（例如第三方 Options/DTO）时，可以使用 `Config.Instance` 进行“按类型绑定”的配置加载/保存，并支持热重载写回（保持外部引用不变）。

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
var options = Config.Instance.Load<ThirdPartyOptions>(attr);

options.Port = 6380;
Config.Instance.Save(options);

// 任何位置都可以再取一次同一引用
var sameRef = Config.Instance.Current<ThirdPartyOptions>();
```

### 重要限制（必须看）

1) `Save()` / `Current()` 依赖缓存的 `ConfigAttribute`

- `Config.Instance.Save<T>()` 与 `Config.Instance.Current<T>()` 会从内部缓存中取 `ConfigAttribute`。
- 因此必须先对该类型调用一次 `Config.Instance.Load<T>(ConfigAttribute)`。

2) “热重载”会写回你手里的对象引用（不替换引用）

`Config.Instance.Load<T>()` 会启动热重载器；当文件被外部修改时，会重新加载一个“新模型”，然后将其公共属性值写回到缓存实例（即你手里的 `options`）。

这意味着：

- 外部修改文件后，你原来持有的 `options` 将被更新（属性写回）。
- 如果你需要“外部变更后自动替换实例”的语义，请使用 `Config<T>.Current`（它会替换 `Current` 引用）。

3) `T` 的限制与写回范围

- 泛型约束：`where T : class, new()`（引用类型 + 公共无参构造）。
- 序列化要求：`T` 必须能被所选 Provider 序列化/反序列化（通常要求公共属性可读写）。
- 写回规则：仅处理公共实例、可读可写、非索引器属性；字段、只读属性、索引器、非公共属性不会被写回。
- 写回方式：浅拷贝（属性值直接赋值；引用类型属性会整体替换该引用）。

4) 绑定粒度：同一类型仅支持一个 ConfigAttribute

- `Config` 内部按 `Type` 缓存 `ConfigAttribute` 与配置实例。
- 同一个 `T` 无法同时绑定到多个不同路径/不同 Provider（后一次 `Load<T>()` 会覆盖缓存的 `ConfigAttribute`）。

## 许可证

MIT，见 [BugFree.Configuration/LICENSE](BugFree.Configuration/LICENSE)。
