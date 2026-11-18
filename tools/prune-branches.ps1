#Requires -Version 5.1
<#
.SYNOPSIS
    批量清理本地分支，仅保留白名单内的分支。

.DESCRIPTION
    保留分支：main, dev-4.x, dev-3.x, dev-2.x, dev-1.x, dev-1.x-no-xml, dev

    默认 DryRun：只打印将要删除的分支，不做任何修改。
    确认无误后加 -Execute 才真正删除。
    默认使用安全删除 git branch -d（未合并会失败并跳过），
    加 -Force 则改用 git branch -D（强制删除未合并分支）。

    多轮安全检查：
      1. 确认当前目录位于 git 工作树内
      2. 白名单自身校验：非空、无空名、无重复、无通配符
      3. 白名单中的分支必须全部真实存在（防拼写错误）
      4. 待删集合 = 全部分支 - 白名单；校验无交集、数量吻合
      5. 执行模式：当前分支必须在白名单内（禁止删除当前分支）
      6. 执行模式：工作区必须干净
      7. 删除前逐个分支再次校验（仍存在、不在白名单、非当前分支）
      8. 删除后复查：剩余分支必须与白名单完全一致

.EXAMPLE
    .\tools\prune-branches.ps1
    DryRun，列出待删分支。

.EXAMPLE
    .\tools\prune-branches.ps1 -Execute
    真正删除，仅删除已合并分支（-d）。

.EXAMPLE
    .\tools\prune-branches.ps1 -Execute -Force
    真正删除，强制删除未合并分支（-D）。
#>
[CmdletBinding()]
param(
    [switch]$Execute,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$keep = @(
    'main'
    'dev-4.x'
    'dev-3.x'
    'dev-2.x'
    'dev-1.x'
    'dev-1.x-no-xml'
    'dev'
)

function Get-LocalBranches {
    $out = @(git for-each-ref --format='%(refname:short)' refs/heads)
    if ($LASTEXITCODE -ne 0) { throw 'git for-each-ref 执行失败，中止。' }
    return @($out | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Get-CurrentBranch {
    $cur = (git rev-parse --abbrev-ref HEAD 2>$null)
    if ($LASTEXITCODE -ne 0) { throw '无法获取当前分支，中止。' }
    return $cur.Trim()
}

# ---------- 检查 1：仓库环境 ----------
$insideWorkTree = (git rev-parse --is-inside-work-tree 2>$null)
if ($LASTEXITCODE -ne 0 -or $insideWorkTree -ne 'true') {
    throw '当前目录不在 git 工作树内，中止。'
}

# ---------- 检查 2：白名单自身校验 ----------
if ($keep.Count -eq 0) { throw '保留名单为空，中止。' }
foreach ($b in $keep) {
    if ([string]::IsNullOrWhiteSpace($b)) { throw '保留名单包含空分支名，中止。' }
    if ($b -match '[*?\[]') { throw "保留名单包含通配符或非法字符：$b，中止。" }
}
$dupes = @($keep | Group-Object | Where-Object { $_.Count -gt 1 })
if ($dupes.Count -gt 0) {
    throw "保留名单存在重复项：$($dupes.Name -join ', ')，中止。"
}

$all = Get-LocalBranches
if ($all.Count -eq 0) { throw '未获取到任何本地分支，中止。' }

# ---------- 检查 3：白名单分支必须全部存在 ----------
$missingKeep = @($keep | Where-Object { $all -notcontains $_ })
if ($missingKeep.Count -gt 0) {
    throw "保留分支不存在：$($missingKeep -join ', ')，中止（防止拼写错误导致误删）。"
}

# ---------- 检查 4：计算待删集合并做集合一致性校验 ----------
$toDelete = @($all | Where-Object { $keep -notcontains $_ })
$overlap  = @($toDelete | Where-Object { $keep -contains $_ })
if ($overlap.Count -gt 0) {
    throw "待删集合与保留名单存在交集：$($overlap -join ', ')，中止。"
}
if (($toDelete.Count + $keep.Count) -ne $all.Count) {
    throw "集合数量校验失败：待删 $($toDelete.Count) + 保留 $($keep.Count) != 全部 $($all.Count)，中止。"
}
$toDelete    = @($toDelete | Sort-Object)
$notOnKeep   = @($all | Where-Object { $keep -notcontains $_ } | Sort-Object)
if (Compare-Object -ReferenceObject $toDelete -DifferenceObject $notOnKeep) {
    throw '两次独立计算结果不一致，中止。'
}

$current = Get-CurrentBranch

# ---------- 输出计划 ----------
Write-Host ''
Write-Host ("保留 ({0})：" -f $keep.Count) -ForegroundColor Green
$keep | ForEach-Object { Write-Host ("  + " + $_) -ForegroundColor Green }
Write-Host ''
Write-Host ("待删除 ({0})：" -f $toDelete.Count) -ForegroundColor Yellow
$toDelete | ForEach-Object { Write-Host ("  - " + $_) -ForegroundColor Yellow }
Write-Host ''
Write-Host ("当前分支：{0}" -f $current)
Write-Host ''

if ($keep -notcontains $current) {
    Write-Warning "当前分支 '$current' 不在保留名单中：执行删除前必须先切换到保留名单内的分支。"
}

if (-not $Execute) {
    Write-Host '[DryRun] 未做任何修改。确认清单无误后，加 -Execute 运行才会真正删除。' -ForegroundColor Cyan
    Write-Host '[DryRun] 默认 -d 仅删除已合并分支；确需删除未合并分支再加 -Force（-D）。' -ForegroundColor Cyan
    exit 0
}

# ---------- 检查 5：当前分支必须在白名单内 ----------
if ($keep -notcontains $current) {
    throw "当前分支 '$current' 不在保留名单中，拒绝删除（请先 git switch 到保留分支）。"
}

# ---------- 检查 6：已跟踪文件不得有未提交改动 ----------
# 未跟踪文件与删除分支无关，仅提示，不阻断
$dirty = @(git status --porcelain --untracked-files=no)
if ($LASTEXITCODE -ne 0) { throw 'git status 执行失败，中止。' }
if ($dirty.Count -gt 0) {
    throw '存在已跟踪文件的未提交改动，中止。'
}
$untracked = @(git status --porcelain | Where-Object { $_ -match '^\?\?' })
if ($untracked.Count -gt 0) {
    Write-Warning "存在 $($untracked.Count) 个未跟踪文件（不影响删除分支，继续）。"
}

$flag = if ($Force) { '-D' } else { '-d' }
Write-Host ("开始删除，使用 git branch {0} ..." -f $flag) -ForegroundColor Cyan

$failed = @()
foreach ($b in $toDelete) {
    # ---------- 检查 7：逐个分支删除前再次校验 ----------
    $now = Get-LocalBranches
    if ($now -notcontains $b) {
        Write-Warning "分支 '$b' 已不存在，跳过。"
        continue
    }
    if ($keep -contains $b) {
        throw "严重错误：'$b' 属于保留名单，立即中止。"
    }
    if ((Get-CurrentBranch) -eq $b) {
        throw "严重错误：'$b' 是当前分支，立即中止。"
    }

    # 临时放宽 EAP，避免 git 写 stderr（如未合并报错）被当成终止错误
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    git branch $flag -- $b
    $rc = $LASTEXITCODE
    $ErrorActionPreference = $prevEap

    if ($rc -ne 0) {
        $failed += $b
        Write-Warning "删除失败（可能未合并，可用 -Force）：$b"
    } else {
        Write-Host ("  deleted " + $b) -ForegroundColor Green
    }
}

# ---------- 检查 8：删除后复查 ----------
$after = Get-LocalBranches
$missingAfter = @($keep | Where-Object { $after -notcontains $_ })
$extraAfter   = @($after | Where-Object { $keep -notcontains $_ })

Write-Host ''
Write-Host ("删除完成：成功 {0}，失败 {1}，剩余分支 {2}" -f ($toDelete.Count - $failed.Count), $failed.Count, $after.Count)

if ($missingAfter.Count -gt 0) {
    throw "复查失败：保留分支丢失 $($missingAfter -join ', ')"
}
if ($extraAfter.Count -gt 0) {
    Write-Warning "仍有非保留分支残留：$($extraAfter -join ', ')"
}
if ($failed.Count -gt 0) {
    throw "有 $($failed.Count) 个分支删除失败：$($failed -join ', ')"
}

Write-Host '复查通过：当前本地分支与保留名单完全一致。' -ForegroundColor Green
