## RPA工具软件需求规格草案（可直接开发）

版本：`v0.1`  
目标：先完成一个可运行的 `MVP`，支持“指令编辑 -> 参数配置 -> 执行 -> 输出传递 -> 保存复用”。

---

## 1. 产品目标与范围

- 构建一个通用自动化执行平台，支持业务自动化与测试自动化。
- 用户通过“指令流程”编排自动化任务。
- 每条指令可绑定：
  - 内置/插件化 C# 方法
  - 外部脚本（首期建议仅一种脚本语言）
- 支持输入参数映射、输出结果回传、变量在流程内传递。
- 支持流程保存、加载、版本管理（MVP先做本地版本）。

**MVP不做**：多节点分布式执行、复杂权限系统、动作市场云端分发。

---

## 2. 角色与典型场景

### 2.1 角色
- `流程设计者`：编排与调试流程。
- `动作开发者`：开发 C# 动作/脚本模板。
- `执行者`：运行流程并查看结果日志。

### 2.2 场景
- 自动化测试：登录系统 -> 调接口 -> 校验返回 -> 输出报告。
- 运维自动化：采集日志 -> 分析关键字 -> 告警/写入结果。
- 数据处理自动化：读取文件 -> 转换 -> 入库/导出。

---

## 3. 功能需求（MVP）

### 3.1 流程编辑
- 支持新增/删除/排序指令步骤。
- 支持 4 类控制结构：`顺序`、`If`、`ForEach`、`TryCatch`。
- 每个步骤可配置：名称、超时、重试次数、失败策略。

### 3.2 动作绑定
- 步骤可绑定到：
  - `C# 动作`（反射注册）
  - `脚本动作`（PowerShell 或 Python 二选一）
- 展示动作元数据：动作名、说明、输入参数定义、输出定义。

### 3.3 参数系统
- 输入值来源：
  - 常量
  - 变量引用（如 `{{OrderId}}`）
  - 简单表达式（MVP可选）
- 输出映射：
  - 动作返回值写入上下文变量（如 `context["LoginResult"]`）。

### 3.4 执行与调试
- 支持：运行全部、单步执行、停止执行。
- 每步记录：开始时间、结束时间、状态、耗时、输入快照、输出快照、异常。
- 支持断点（MVP 可先支持“从指定步骤开始执行”代替完整断点）。

### 3.5 持久化
- 流程保存为 JSON 文件。
- 支持流程导入/导出。
- 保存时做结构校验（Schema 校验）。

### 3.6 日志与报告
- 控制台日志 + 文件日志。
- 执行完成生成报告（MVP 为 JSON/Markdown）。

---

## 4. 非功能需求

- **稳定性**：每步必须有超时保护；异常不导致进程崩溃。
- **可扩展性**：动作通过统一接口注册，不改引擎即可新增。
- **可观测性**：日志带 `RunId`、`StepId`、`ActionName`。
- **安全性**：脚本执行限制目录与可访问资源；敏感参数脱敏。
- **性能**：100 步以内流程启动时间 < 2 秒（本地开发机目标）。

---

## 5. 技术方案（建议）

- 桌面端：`WPF + MVVM`
- 引擎层：`.NET Class Library`（可复用到后续服务化）
- 数据存储：MVP 用 `SQLite`（流程元数据+运行记录）+ JSON 文件（流程定义）
- 脚本执行：首期仅一种（建议 PowerShell，Windows 兼容与运维场景友好）

---

## 6. 核心数据模型（建议）

### 6.1 流程定义（示例）
```json
{
  "flowId": "flow_login_test",
  "name": "登录与校验流程",
  "version": "1.0.0",
  "variables": {
    "BaseUrl": "https://example.com",
    "UserName": "admin"
  },
  "steps": [
    {
      "id": "step1",
      "type": "CallMethod",
      "action": "HttpPost",
      "inputs": {
        "url": "{{BaseUrl}}/login",
        "body": {
          "user": "{{UserName}}",
          "password": "{{Password}}"
        }
      },
      "outputs": {
        "token": "AuthToken"
      },
      "timeoutMs": 10000,
      "retry": 1,
      "onError": "Stop"
    }
  ]
}
```

### 6.2 执行上下文
- `RunId`
- `Variables: Dictionary<string, object?>`
- `StepResults: List<StepExecutionResult>`
- `CancellationToken`

### 6.3 步骤执行结果
- `StepId`
- `Status`（Success/Failed/Skipped/Timeout）
- `DurationMs`
- `InputSnapshot`
- `OutputSnapshot`
- `Error`

---

## 7. 核心接口定义（建议）

```csharp
public interface IActionHandler
{
    string Name { get; }
    ActionMetadata Metadata { get; }
    Task<ActionResult> ExecuteAsync(ActionRequest request, ExecutionContext context, CancellationToken ct);
}
```

```csharp
public sealed class ActionRequest
{
    public required string StepId { get; init; }
    public required Dictionary<string, object?> Inputs { get; init; }
    public int TimeoutMs { get; init; } = 30000;
}
```

```csharp
public sealed class ActionResult
{
    public bool Success { get; init; }
    public Dictionary<string, object?> Outputs { get; init; } = new();
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
```

---

## 8. 模块拆分（代码落地）

- `FlowDesigner`：流程编辑 UI
- `FlowEngine`：流程解析、调度、执行
- `ActionSdk`：动作接口、元数据、注册机制
- `ActionBuiltin`：内置动作（文件、HTTP、延时、断言）
- `ScriptHost`：脚本执行适配层
- `Persistence`：JSON/SQLite 读写
- `RuntimeMonitor`：日志、报告、运行态事件

---

## 9. 开发里程碑（8周）

- **W1-W2**：流程 DSL + Schema + 执行上下文
- **W3-W4**：执行引擎（顺序/If/ForEach/TryCatch）
- **W5**：C# 动作注册与调用
- **W6**：脚本动作与超时隔离
- **W7**：编辑器 UI + 保存加载
- **W8**：日志报告 + 联调 + 验收

---

## 10. 验收标准（MVP）

- 能创建并保存一个包含 10+ 步的流程。
- 能绑定至少 5 个内置 C# 动作（文件、HTTP、变量、断言、延时）。
- 能执行脚本动作并将输出回传给后续步骤。
- 支持失败重试与失败终止策略。
- 生成完整运行日志与结果报告。
- 有最少 20 个单元测试 + 5 个端到端流程测试。

---

如果你愿意，我下一步可以直接给你 **`v0.1 开发任务分解清单（可贴到 Jira/禅道）`**，按模块拆到“接口、页面、测试用例、完成定义（DoD）”级别。