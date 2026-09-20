# F09 设计草案 · 操作状态与幂等契约

> 状态：**已实现（2026-09-20）**，三个待拍板项按草案里的倾向定了：
> `requestKey` 不强制、保留 200 条 / 24 小时、`operationId` 注入 `structuredContent`。
> 离线测试覆盖见 §9。真实 Revit 验收 9 项全过（幂等重放、同键异参、状态查询、查不到→unknown）；
> **超时那一条只有单测**——74 个工具全部显式声明了 TimeoutSeconds，真机上造不出超时，
> 改为注入抛 DispatchTimeoutException 的调度器来验。
> 针对《源码评估与改进报告（2026-09-20）》F09。
> 相关已完成项：提交前的取消检查（M10-F）、`expectedDocumentId` 前置校验（M10-D）。

---

## 1. 要解决的到底是哪个问题

调度器已经能区分"没开始"（`REVIT_BUSY`）和"可能已执行"（`TIMEOUT`）——这一步是对的。
问题在**它之后**：客户端拿到 `TIMEOUT` 以后，没有任何接口能回答"那到底建成了没有"。

现在只能靠人工核对；而 Agent 在这种情况下最常做的两件事都是错的：

- **直接重试** → 模型里多出一整套重复构件。它们和第一套长得一模一样，
  平面上看不出来，要到明细表数量对不上时才暴露。
- **当作失败继续** → 后续构件挂在一批"不存在"的宿主上，报一串 `ELEMENT_NOT_FOUND`。

超时不是罕见情况：一次批量建 200 个构件、一次大模型的 `revit_export_documents`，
都可能撞上默认 60 秒。**而在 Revit 里"重复创建"是最难回滚的那类错误**——
它不报错、不产生警告，撤销栈里也只是一步普通的创建。

### 不在本设计范围内

- **跨进程 / 跨中心文件的协调**：本服务是 Revit 进程内的单实例，不解决多人协同的幂等。
- **exactly-once**：做不到，也不该宣称。见 §7。
- **F10 的回执体积**：另案。

---

## 2. 契约总览

三件东西，缺一不可：

| # | 东西 | 谁给 | 作用 |
|---|---|---|---|
| 1 | `operationId` | 服务端生成 | 每次 `tools/call` 一个。**成功失败都返回**，是后续一切查询的抓手 |
| 2 | `requestKey` | 客户端可选给 | 幂等键。同键同内容 → 返回原结果，不重复执行 |
| 3 | `revit_get_operation_status` | 新增只读工具 | 按 `operationId` 或 `requestKey` 查状态与结果 |

### 状态机

```
                 ┌──────────────► cancelled（排队期间被取消，模型未被触碰）
                 │
queued ──────► running ──┬──► committed    事务已提交，改动在模型里
                         ├──► rolledBack   事务已回滚，模型原样（含工具自己抛错）
                         ├──► failed       没进到事务就失败（参数错、写保护、目标文档不符）
                         └──► unknown      **执行中失联**：超时、Revit 崩溃、进程被关
```

`unknown` 是这套设计存在的理由，**不是兜底**。它必须是一个一等状态：
带着"接下来怎么查证"的指引，而不是一句"未知"。

---

## 3. 幂等键

### 键怎么算

```
identity = SHA256( toolName ‖ requestKey ‖ expectedDocumentId ‖ canonical(arguments) )
```

- `canonical(arguments)`：递归按 key 排序、无空白的 JSON 序列化，**剔除 `requestKey` 自身与 `_meta`**。
  本项目自带 JSON 实现，加一个 `JsonValue.ToCanonicalJson()` 即可，不引入依赖。
- `expectedDocumentId` 进哈希：同一批参数打到另一个文档上是**另一次操作**，不能复用结果。
  没给 `expectedDocumentId` 时用执行时的活动文档身份（`ContextIdentity.Key`）。

### 三种情形

| 情形 | 行为 |
|---|---|
| 键没见过 | 正常执行，登记进日志 |
| **同键同内容** | **不执行**，原样返回上次的回执，并加一条 warning 说明"这是重放" |
| 同键不同内容 | 拒绝：`IDEMPOTENCY_CONFLICT`。报错里给出上次那条操作的 `operationId` 与时间 |

同键不同内容之所以要拒绝而不是"当新请求执行"：那几乎总是客户端的 bug
（复用了键却改了参数），静默执行会造出一个谁都没预期的东西。

### 重放一条"还在 running"的键

并发只可能来自不同的 HTTP 连接（Revit 主线程本身是串行的）。
此时返回 `OPERATION_IN_FLIGHT` 与 `operationId`，让调用方去查状态，
**不排队等它**——那只会把第二个连接也拖到超时。

---

## 4. 操作日志（journal）

### 存什么

```csharp
sealed class OperationRecord {
    string   OperationId;      // "op_" + 12 字节随机（base64url）
    string   RequestKey;       // 客户端给的，可空
    string   IdentityHash;     // §3 的哈希
    string   ToolName;
    string   DocumentKey;      // 执行时的活动文档
    DateTime QueuedAtUtc, StartedAtUtc?, EndedAtUtc?;
    OperationState State;
    string   ErrorCode;        // 失败时
    string   ResultJson;       // 成功时的 structuredContent，**带上限**（见下）
    string[] AffectedElementIds;   // 便于"到底建了什么"
    string[] SideEffects;          // 落盘/导出这类事务管不到的副作用
}
```

`ResultJson` 设 **64 KB 上限**：超了只留 `affectedElementIds` 与计数，
并在重放时说明"原回执过大，未保留全文，请用 ID 查询"。
理由是重放的价值主要在"别重复建"，不在"一字不差地拿回那份 JSON"。

### 放哪儿、留多久

- **进程内环形缓冲**，容量 **200 条**或 **24 小时**，先到先淘汰。
- 写在两个地方：状态流转发生在主线程（`ToolPipeline` 的调度回调里），
  `queued` 发生在 HTTP 线程。加一把锁足够——量级是每秒个位数。
- 同时**往现有审计日志里追加一行**（`Log.Audit`），只为事后取证，不参与查询。

### 为什么不持久化到磁盘

想清楚了再决定不做，理由不是"省事"：

1. **持久化也回答不了最关键的那个问题。** 崩溃恰好发生在 `Commit()` 内部时，
   日志里只有 `running`——磁盘上的记录和内存里的记录一样说不出它到底落没落盘。
2. **模型才是唯一的事实来源。** Revit 重启后要确认某次操作有没有生效，
   正确做法是查模型（`revit_get_model_changes` / 按类别位置查构件），
   而不是信一份可能比模型更旧的日志。
3. 一份"看起来权威、其实可能撒谎"的持久化日志，比没有更危险。

**因此明确写进契约**：进程重启后 journal 为空，查任何旧 `operationId` 一律返回
`unknown` + 查证指引。如果将来要做持久化，它解决的是"跨会话审计"，不是"幂等保证"。

---

## 5. 接口变化

### 5.1 入参：`requestKey`（管线级保留参数）

和 `expectedDocumentId` 同一条路子：不写进每个 Input DTO，由管线在绑定前摘出来，
`SchemaGenerator` 给**写工具**的 schema 统一补上。

```json
"requestKey": {
  "type": "string",
  "description": "幂等键。同一个键 + 同样的参数只会执行一次；重试时带上它，就不会重复创建。超时后重试尤其要带——那时你无法确定上一次到底执行了没有。"
}
```

**可选**。不给就是现在的行为，一个字节都不变。

### 5.2 回执：`operationId`

复用现有的 warnings 注入机制（`AttachWarnings` 那一层），
给**写工具**的 `structuredContent` 注入一个字段：

```json
{ "created": 9, "elements": [...], "operationId": "op_7Qk2..." }
```

只读工具不注入——它们没有可查的状态，加了只是噪音。

### 5.3 失败时也要给

`TIMEOUT` / `REVIT_BUSY` / 任何异常的错误文本里都要带 `operationId`，
否则最需要它的那一刻恰恰拿不到。错误文案改成：

```
TIMEOUT: 工具 revit_create_line_based_elements 执行超过 60 秒，结果不确定。
operationId=op_7Qk2...
先用 revit_get_operation_status 查它的状态，**不要直接重试**——
重复创建在 Revit 里最难发现：不报错、不产生警告。
```

### 5.4 新工具 `revit_get_operation_status`（只读）

入参 `operationId` 或 `requestKey`（二选一）。回执：

| 字段 | 说明 |
|---|---|
| `state` | queued / running / committed / rolledBack / failed / cancelled / unknown |
| `toolName`、`documentId`、`queuedAt`、`startedAt`、`endedAt` | 基本信息 |
| `affectedElementIds` | committed 时"到底建/改了什么" |
| `sideEffects` | 落盘、导出这类事务回滚不掉的动作 |
| `result` | 原回执（≤64 KB 时） |
| `nextStep` | **一句可执行的话**，见下表 |

`nextStep` 按状态给：

| state | nextStep |
|---|---|
| committed | 已生效，继续下一步；构件 ID 在 affectedElementIds 里 |
| rolledBack / failed | 模型未改动，修正参数后可以重试（带同一个 requestKey 也行） |
| cancelled | 排队期间被取消，模型未被触碰 |
| running | 还在跑，等它结束再查；不要另发一次 |
| unknown | **不要直接重建**。用 `revit_get_model_changes` 或按类别+位置查一遍那批构件；查不到再重试 |

---

## 6. 与取消的关系

M10-F 已经补上"提交前再查一次取消"。本设计把取消也纳入状态机：

| 时刻 | 行为 | 终态 |
|---|---|---|
| 还在队列里 | 直接丢弃，不进主线程 | `cancelled` |
| 执行中（工具循环有检查点） | 抛 `OperationCanceledException`，事务回滚 | `rolledBack` |
| 执行完、提交前 | 回滚（M10-F 已实现） | `rolledBack` |
| **已提交** | **不能谎称回滚** | `committed` |

最后一行是硬约束：提交之后再收到取消，状态就是 `committed`，
日志和回执都要这么说。"客户端断开了所以当作没发生"是不诚实的——改动真的在模型里。

---

## 7. 明确不承诺什么

写进文档，也写进工具说明：

1. **不是 exactly-once**，是"在 journal 的保留期与本进程生命周期内，同一个 requestKey 至多执行一次"。
2. **进程重启后幂等保证失效**：旧键查不到，会被当作新请求执行。
   跨重启的安全网只有一条——**先查模型**。
3. **外部副作用不回滚**：`revit_save_document_as`、各类导出写出去的文件，
   事务管不着。它们记在 `sideEffects` 里，重放时**不重新执行**，
   但也不假装文件不存在。
4. **不做排队等待**：撞上同键在飞，立刻返回让调用方去查，不把第二个连接也拖死。

---

## 8. 实施顺序与工作量

| 步 | 内容 | 落在哪 | 可测性 |
|---|---|---|---|
| 1 | `JsonValue.ToCanonicalJson()` + 哈希 | Protocol | 纯单测 |
| 2 | `OperationJournal`（状态机 + 环形缓冲 + 淘汰） | Tooling | 纯单测 |
| 3 | 管线接线：摘 `requestKey`、登记状态、注入 `operationId`、重放 | Tooling | 纯单测（现有 FakeHost 就够） |
| 4 | 错误文案带 `operationId` | Tooling | 纯单测 |
| 5 | `revit_get_operation_status` 工具 | Addin | 契约测试 |
| 6 | 真实 Revit 验收：超时 → 查状态 → 不重复建 | workflows | 需要构造一次真超时 |

前四步不依赖 Revit，可以先做先验。第 6 步的超时要真的造出来——
建议用一个大批量创建 + 把 `defaultToolTimeoutSeconds` 临时调到 1 秒。

## 9. 回归集（报告 §8 点名的那几条）

- 同键同参 → 只执行一次，第二次返回原回执且带"这是重放"的提示；
- 同键异参 → `IDEMPOTENCY_CONFLICT`，且**第二次没有产生任何写入**；
- 无键 → 行为与今天完全一致（防回归）；
- 队列中取消 → `cancelled`，工具一次都没被调用；
- 提交前取消 → `rolledBack`，模型无变化（M10-F 已有，纳入状态断言）；
- 提交后取消 → `committed`，**不谎称回滚**；
- 淘汰后再查 → `unknown` + 查证指引，而不是"没有这个操作"；
- 超时 → 状态是 `unknown`，且 `nextStep` 明确写着"不要直接重建"。

---

## 10. 实现落点（2026-09-20）

| 设计里的东西 | 落在哪 |
|---|---|
| 规范化序列化 + 指纹 | `src/RevitMCP.Protocol/Json/JsonCanonical.cs` |
| 状态机 + 环形缓冲 + 淘汰 | `src/RevitMCP.Tooling/OperationJournal.cs` |
| 摘 `requestKey`、登记、重放、注入 `operationId` | `src/RevitMCP.Tooling/ToolPipeline.cs` |
| schema 里的 `requestKey` | `SchemaGenerator.WithExpectedDocument`（与 `expectedDocumentId` 同一处） |
| 新错误码 | `IDEMPOTENCY_CONFLICT`、`OPERATION_IN_FLIGHT` |
| 状态查询工具 | `src/RevitMCP.Addin/Tools/OperationStatusTool.cs` |
| 测试 | `JsonCanonicalTests`(9) + `IdempotencyTests`(12)，总数 419 → 443 |

两处实现时才定下来的细节：

- **`Complete` 永不覆盖 `Committed`**：提交之后再收到取消，状态仍是已提交。
  "客户端断开了所以当作没发生"是不诚实的——改动真的在模型里。
- **跑起来之前 vs 之后失败**：前者记 `failed`（模型一个字节没动），后者记 `rolledBack`
  （事务回滚了）。分界点就是主线程上前置条件全过、准备调工具的那一刻。
  这两个状态对调用方的意义不同：后者可以原样重试，前者得先把前置条件弄对。

## 11. 需要你拍板的三件事（已定）

> 均按下面"我倾向"的那一项实现。留着是为了记录理由。

1. **`requestKey` 要不要对破坏性工具强制要求？**
   我倾向**不强制**：会让所有现有客户端一夜之间全部报错，收益却只对"会重试的客户端"成立。
   折中方案是：`revit_delete_elements` 这类不带键时在回执里加一条提示。
2. **保留 200 条 / 24 小时够不够？**
   按一次建模会话几十到上百次写调用估的。要更长就得考虑落盘，而落盘的价值有限（§4）。
3. **`operationId` 注入进 `structuredContent`，还是放进结果的 `_meta`？**
   我倾向前者：`_meta` 有些客户端会直接丢掉，而这个字段在超时那一刻是救命的。
   代价是每个写回执多约 30 字节。
