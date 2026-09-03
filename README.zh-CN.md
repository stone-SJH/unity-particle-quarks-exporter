# Unity ParticleSystem 到 Three.Quarks 导出器

[English](README.md) | [简体中文](README.zh-CN.md)

将受支持的 Unity Shuriken ParticleSystem 预制体转换为确定性的
Three.Quarks JSON。项目包含一个可选的 companion runtime，用于补充
stock Quarks 无法表示的行为。

## 前置条件

### Unity 导出器

- Unity `2022.3.52f1` 或 Unity `6000.3.22f1`（Unity 6.3 LTS）。
- Built-in Render Pipeline 或 URP。HDRP 为 `source-only`。
- 一个包含 ParticleSystem 预制体及其引用材质、纹理和网格的 Unity 项目。

导出器可以在不依赖浏览器的情况下写出任一 profile：

| 导出 profile | Unity 侧要求 | 浏览器侧要求 |
| --- | --- | --- |
| `stock` | 本 UPM package | `three@0.185.0`、`three.quarks@0.17.1`、`quarks.core@0.17.1` |
| `extended`（paired） | 本 UPM package | 上述 stock packages，加上 `unity-particle-quarks-runtime@0.3.3` |

当 manifest 没有 required companion extension 时选择 `stock`。当 manifest
要求 `unity_particle_paired_semantics@1` 时选择 `extended`；此时浏览器应用
必须提供 paired runtime package。

`unity_particle_paired_semantics@1` 是一个有版本的 manifest 扩展描述符，
不是额外的 Unity 或 npm package。它标识随 stock Quarks JSON 一起输出的
Unity 特有语义元数据，例如作者设置的 renderer alignment/pivot、
particle-head、simulation-speed、texture-sheet、light 或 limit-velocity
细节。`extensionsUsed` 表示可能存在这类元数据；`extensionsRequired` 表示
该效果依赖 companion adapter 才能应用这些语义。Stock Quarks 仍可解析基础
JSON，但会拒绝声明此扩展为 required 的效果，而不会静默声称 Unity 语义已
完整保留。

### 浏览器 runtime

- Three.js `0.185.0`，作为应用的 peer dependency。
- 两种 profile 都需要 `three.quarks@0.17.1` 和 `quarks.core@0.17.1`。
- 只有 paired/extended manifest 需要
  `unity-particle-quarks-runtime@0.3.3`。
- 只有使用 package build/test 工具时才需要 Node.js `>=18.18.0`。

## 组件

- **Unity exporter**：读取 ParticleSystem 模块、材质、纹理、renderer、trail
  和 sub-emitter，然后写出 JSON、manifest 和 conversion report。
- **Browser runtime**：使用 stock `QuarksLoader` 或 extended companion
  adapter 加载导出的 JSON，并提供 pooling、preload、spawn、update、release
  和 telemetry API。
- **兼容性矩阵**：在
  [`docs/compatibility-matrix.md`](docs/compatibility-matrix.md) 中列出支持的
  editor/pipeline tuple、模块行为、fallback 以及 strict/best-effort 结果。

## Unity 导出器

从 `packages/com.yahaha.particle-quarks-exporter` 安装 UPM package。
使用以下命令运行 batch exporter：

```text
-executeMethod UnityParticleQuarksExporter.Editor.ParticleQuarksExportBatchmode.RunBatch
-particleQuarksConfig <config.json>
```

配置示例：

```json
{
  "schemaVersion": "unity_particle_quarks_pipeline.config.v1",
  "outputRoot": "./exports/unity-vfx",
  "mode": "strict",
  "runtimeProfile": "stock",
  "target": "default",
  "sourceRenderPipeline": "current",
  "maxTextureSize": 1024,
  "effects": []
}
```

使用 `mode: "strict"` 拒绝存在阻断性不支持输入的转换。只有在接受命名的
`partial` fallback 时才使用 `mode: "best-effort"`。`runtimeProfile: "stock"`
输出供普通 Three.Quarks 播放的 JSON；当 manifest 需要时，
`runtimeProfile: "extended"` 启用 companion adapter。

每次可发布的导出都会在 `outputRoot` 下写出两个用途不同的 manifest：

- `manifest.json` 是 pipeline 和诊断记录，可以包含不可播放的 `failed`、
  `profile_required` 或 `review_only` 条目。
- `runtime-manifest.json` 是 runtime 可加载目录。只有全部 effect 都可发布
  （`ready` 或 `partial`）时才会生成，并把导出的 `effectJson` 映射到 runtime
  所需的 `url` 字段。

任一 effect 阻断发布时都不会生成 `runtime-manifest.json`；原子目录替换也会
移除旧文件，避免误加载过期 effect。

## 浏览器 runtime

runtime package 是 `unity-particle-quarks-runtime`，需要 Three.js
`0.185.0`。它支持 stock 和 extended profile：

```sh
npm install unity-particle-quarks-runtime@0.3.3 three@0.185.0
```

如果配置的 registry 尚未提供 `0.3.3`，可从本源码 checkout 构建并安装：

```sh
npm ci
npm pack -w unity-particle-quarks-runtime
npm install ./unity-particle-quarks-runtime-0.3.3.tgz three@0.185.0
```

```ts
import { createVfxRuntime } from 'unity-particle-quarks-runtime';

const runtime = createVfxRuntime({
  scene,
  renderer,
  camera,
  runtimeProfile: 'extended'
});

await runtime.loadManifest('./effects/runtime-manifest.json');
await runtime.preload('water-impact');
const handle = runtime.spawn('water-impact');
runtime.update(deltaSeconds);
runtime.release(handle);
```

当 manifest 没有 required companion extension 时使用
`runtimeProfile: "stock"`。Extended profile 是默认值，并处理
`unity_particle_paired_semantics@1` 元数据。

## 支持范围

声明支持的 editor tuple 为 Unity `2022.3.52f1` 和 Unity `6000.3.22f1`
（Unity 6.3 LTS），每个版本支持 Built-in 或 URP。HDRP 为 `source-only`。
浏览器 runtime 要求 Node.js `>=18.18.0`、Three.js `0.185.0` 以及
`three.quarks`/`quarks.core` `0.17.1`。模块级行为和 fallback 详见
[`兼容性矩阵`](docs/compatibility-matrix.md)。

每次 batch 运行都会在 `outputRoot` 下写出 pipeline manifest 和 conversion
report；全部内容可发布时还会写出 `runtime-manifest.json`。
报告会标识输入效果、转换阶段、预期契约、观测值和下一步动作。
`unknown`、`partial`、`unsupported` 和 `rejected` 始终保持为明确状态。

## 许可证

代码使用 MIT 许可证。第三方依赖沿用各自许可证，详见 `NOTICE` 和各 package
中的 notice 文件。
