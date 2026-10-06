# 一步步上传到 GitHub

本机目前**没有安装 git**（但 `winget` 可用），所以有两条路：**A 网页上传**（无需安装任何东西，
推荐先用这条）和 **B 命令行**（装 git 之后更好维护）。

上传前先确认仓库内容干净 —— `.gitignore` 已经把游戏字节码（`extracted/`）、
分析产物（`disasm/`）、编译产物（`*.exe`）、日志等排除在外。可以自查一下：

```powershell
cd "D:\ProgramData\DeepSeek Harness Workspace\kr-frontiers-trainer"
python -c "import os
skip={'extracted','disasm','.git','__pycache__'}
for root,dirs,files in os.walk('.'):
    dirs[:]=[d for d in dirs if d not in skip]
    for f in files:
        p=os.path.join(root,f)[2:]
        print(f'{os.path.getsize(p):>8}  {p}')"
```

仓库里应该只有这些：

```
   LICENSE
   README.md
   .gitignore
   .gitattributes
   docs/HOW-TO-PUBLISH.md
   docs/REVERSE-ENGINEERING.md
   src/KrTrainer.cs
   src/payload.lua
   src/app.manifest
   src/build.bat
   tools/*.py
```

---

## 路线 A：网页上传（不用装任何东西）

### A1. 注册 / 登录

打开 <https://github.com/signup> 注册（已有账号就直接登录）。

### A2. 新建仓库

1. 右上角 **`+`** → **New repository**
2. 填写：
   - **Repository name**：`kingdom-rush-frontiers-trainer`
   - **Description**：`王国保卫战：前线 (Steam) 单文件修改器 - 无限金币/生命/秒杀敌人 + 存档修改`
   - 选 **Public**
   - **不要**勾 "Add a README file"、**不要**选 .gitignore 和 license（我们本地已经有了，勾了会冲突）
3. 点 **Create repository**

### A3. 上传文件

在新仓库页面点 **uploading an existing file**（或 `Add file` → `Upload files`）。

1. 打开资源管理器进 `D:\ProgramData\DeepSeek Harness Workspace\kr-frontiers-trainer`
2. **把 `src`、`tools`、`docs` 三个文件夹和 `README.md`、`LICENSE`、`.gitignore` 一起选中，拖进网页的上传框**
   - ⚠️ 不要拖 `extracted`、`disasm` 这两个目录（是游戏版权内容，体积也大）
   - `.gitignore` 是隐藏文件，先在资源管理器「查看」里勾上「隐藏的项目」才看得到
3. 等文件列表加载完，下方 **Commit changes** 填一句说明，例如
   `feat: 王国保卫战：前线 单文件修改器 v1.0`，点 **Commit changes**

> 网页上传限制：单次最多 100 个文件、单个文件最大 25 MB。本仓库只有约 20 个小文件，没问题。

### A4. 检查

刷新仓库首页，README 应该自动渲染出来。确认 `extracted/`、`disasm/`、`*.exe`
**没有**被传上去。

### A5. 发布 Release（让别人能直接下载 exe）

1. 仓库右侧 **Releases** → **Create a new release**
2. **Choose a tag** 填 `v1.0`，点 **Create new tag**
3. **Release title** 填 `v1.0 - 首个版本`
4. 描述里可以写更新内容
5. 把 `src\KrTrainer.exe` **拖进** "Attach binaries" 区域
6. 点 **Publish release**

README 里那条"下载 Releases 页面"的说明就生效了。

---

## 路线 B：命令行（先装 git）

### B1. 安装 git

```powershell
winget install --id Git.Git -e --source winget
```

装完**关掉再重开** PowerShell，然后验证：

```powershell
git --version
```

### B2. 配置身份（只需一次）

```powershell
git config --global user.name "你的名字"
git config --global user.email "你的邮箱"
git config --global core.quotepath false     # 让中文文件名正常显示
```

### B3. 本地建仓并提交

```powershell
cd "D:\ProgramData\DeepSeek Harness Workspace\kr-frontiers-trainer"

git init
git branch -M main

git add .
git status                    # ← 检查一遍: 不该有 extracted/ disasm/ *.exe

git commit -m "feat: 王国保卫战：前线 单文件修改器 v1.0"
```

如果 `git status` 里出现了不该有的文件，说明 `.gitignore` 没生效，检查文件名是不是
真的叫 `.gitignore`（不是 `.gitignore.txt`）。

### B4. 在 GitHub 上建一个**空**仓库

同 A2，但**什么都不要勾**（不勾 README、不选 .gitignore、不选 license）。

### B5. 关联并推送

把下面的 `<你的用户名>` 换成你的 GitHub 用户名：

```powershell
git remote add origin https://github.com/<你的用户名>/kingdom-rush-frontiers-trainer.git
git push -u origin main
```

**关于密码**：GitHub 早就不接受账号密码推送了，弹出凭据窗口时：
- 用户名填 GitHub 用户名
- 密码填 **Personal Access Token**（不是登录密码）

生成 Token：GitHub 右上角头像 → **Settings** → 左侧最下 **Developer settings**
→ **Personal access tokens** → **Tokens (classic)** → **Generate new token (classic)**
→ 勾选 **`repo`** → 生成后**立刻复制**（页面关掉就再也看不到了）。

Windows 上第一次填完会被「凭据管理器」记住，之后就不用再输了。

### B6. 后续更新

```powershell
git add .
git commit -m "fix: 修了 xxx"
git push
```

---

## 常见问题

**推送时报 `failed to push some refs` / `rejected`**
远端仓库不是空的（建仓时勾了 README）。两种做法：
```powershell
git pull --rebase origin main     # 先把远端拉下来合并
git push
```
或者删掉 GitHub 上的仓库重建一个空的。

**中文文件名 / 提交信息乱码**
```powershell
git config --global core.quotepath false
git config --global i18n.commitEncoding utf-8
```

**想改仓库名或描述**
仓库页面 **Settings** → 最上面的 **Rename** / **Description** 直接改。

**想加速 clone / push**
国内网络可以配代理：
```powershell
git config --global http.proxy http://127.0.0.1:7890
git config --global https.proxy http://127.0.0.1:7890
```
端口换成你自己代理的。

**想用 GitHub CLI（更省事）**

```powershell
winget install --id GitHub.cli -e
gh auth login
gh repo create kingdom-rush-frontiers-trainer --public --source . --push
gh release create v1.0 "src\KrTrainer.exe" --title "v1.0" --notes "首个版本"
```

一条 `gh repo create` 就把建仓 + 关联 + 推送全做完了，不需要手填 Token。
