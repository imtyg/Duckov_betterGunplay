# BetterGunPlay Mod
## 简介

BetterFire 是一个针对 Duckov 的枪械手感优化模组。

### 主要功能 

- 优化奔跑状态的开火逻辑
- 按住左键时自动中断互动动作
- 确保玩家能够第一时间进行射击

### 安装（仅使用编译版本）

1. 前往 [Releases](https://github.com/p1916531749-spec/Duckov_betterGunplay/releases) 页面下载最新版本
2. 将所有文件复制到游戏的 Mods 目录
3. 启动游戏，模组将自动加载

### 从源码构建

#### 前置要求

- [.NET SDK 6.0 或更高版本](https://dotnet.microsoft.com/download)
- Visual Studio 2019/2022 或 JetBrains Rider（推荐）
- 游戏的主程序集引用（Assembly-CSharp.dll 等）

#### 构建步骤

1. 克隆仓库：
```bash
git clone https://github.com/p1916531749-spec/Duckov_betterGunplay.git
cd Duckov_betterGunplay
```

2. 打开 `Source/BetterFire.csproj` 项目文件

3. 确保项目引用了正确的游戏程序集路径（在 `.csproj` 文件中配置）

4. 构建项目：
```bash
cd Source
dotnet build -c Release
```

5. 编译后的 `BetterFire.dll` 将在项目根目录中生成

## 项目结构

```
Duckov_betterGunplay/
├── Source/                 # 源代码目录
│   ├── BetterFire.csproj   # 项目文件
│   └── ModBehaviour.cs     # 主模组逻辑
├── info.ini                # 模组信息配置文件
├── BetterFire.dll          # 编译后的程序集（运行版本）
├── BetterFire.deps.json    # 依赖配置文件
├── README.md               # 本文件
└── LICENSE                 # 许可证文件
```

## 配置

模组配置通过 `info.ini` 文件进行：

```ini
name=BetterFire
displayName=更好的开火v1.1
description=优化奔跑状态的开火逻辑，并在按住左键时中断互动，确保能够第一时间射击。
```

高级配置可通过 `StreamingAssets/BetterFire.cfg` 文件进行（由模组运行时创建）。

## 开发

### 代码结构

- `ModBehaviour.cs`: 实现了主要的模组逻辑，包括：
  - 奔跑状态开火优化
  - 互动取消机制
  - 配置管理

### 依赖项

- Assembly-CSharp.dll（游戏主程序集）
- TeamSoda.Duckov.Core.dll
- TeamSoda.Duckov.Utilities.dll
- ItemStatsSystem.dll
- Unity Engine 相关程序集


