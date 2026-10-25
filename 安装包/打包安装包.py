# -*- coding: utf-8 -*-
# 打「PCL 枪战突击修改版」单文件安装程序（一个 exe，双击即装）。
#
# 流程（与枪战突击客户端安装器同一套）：
#   1) 收集要内嵌的文件（PCL 主程序 + 3 个 dll + config + xml）复制到 ASCII 临时目录
#      （windres 不认中文文件名，必须走这一步）
#   2) 生成 清单.txt 和 安装器资源.rc
#   3) windres 编译资源 → 资源.o
#   4) gcc 链接 安装器.c + 资源.o → PCL枪战突击修改版_安装程序.exe
import os, io, shutil, subprocess, sys

这里 = os.path.dirname(os.path.abspath(__file__))
# PCL 版产物目录（Actions 下载解压到这里）
产物目录 = os.path.join(os.path.dirname(这里), '..', '_PCL产物', 'PCL-QiangTuEdition')
产物目录 = os.path.abspath(产物目录)
临时 = os.path.join(os.environ.get('TEMP', 'C:\\Windows\\Temp'), 'pcl_pkg')  # ⚠️ 必须整条路径 ASCII：windres 不认中文目录
输出 = os.path.join(这里, 'PCL枪战突击修改版_安装程序.exe')

GCC = r'D:\msy\mingw64\bin\gcc.exe'
WINDRES = r'D:\msy\mingw64\bin\windres.exe'

文件们 = []


def 收(磁盘路径, 相对路径):
    if os.path.isfile(磁盘路径):
        文件们.append((磁盘路径, 相对路径.replace('\\', '/')))


# PCL 版需要的全部文件（exe 缺 dll 会起不来）
for 名 in ['Plain Craft Launcher 2.exe', 'Plain Craft Launcher 2.exe.config', 'Plain Craft Launcher 2.xml',
           'MeloongCore.dll', 'MeloongCore.Wpf.dll', 'PCLCS.dll', 'Microsoft.Win32.Registry.dll']:
    收(os.path.join(产物目录, 名), 名)

assert 文件们, '没找到 PCL 产物：请先跑 GitHub Actions 并把产物解压到 _PCL产物/PCL-QiangTuEdition'
print('待内嵌文件 %d 个' % len(文件们))

# 1) ASCII 临时目录（000001.bin …），中文路径全躲开
if os.path.exists(临时):
    shutil.rmtree(临时)
os.makedirs(临时)

清单行 = []
for i, (磁盘, 相对) in enumerate(文件们, start=1):
    临时名 = '%06d.bin' % i
    shutil.copyfile(磁盘, os.path.join(临时, 临时名))
    清单行.append('%d\t%s\t%d' % (i, 相对, os.path.getsize(磁盘)))
io.open(os.path.join(临时, 'manifest.txt'), 'w', encoding='utf-8', newline='\n').write('\n'.join(清单行) + '\n')

# 2) 生成 rc：清单 + 所有文件都以 RCDATA 内嵌
rc = ['1 ICON "icon.ico"']
rc.append('900 RCDATA "manifest.txt"')
for i in range(1, len(文件们) + 1):
    rc.append('%d RCDATA "%06d.bin"' % (i, i))
io.open(os.path.join(临时, 'res.rc'), 'w', encoding='utf-8', newline='\n').write('\n'.join(rc) + '\n')

# 3) 资源编译（⚠️ 工作目录必须在 ASCII 临时目录内）
shutil.copyfile(os.path.join(这里, '图标.ico'), os.path.join(临时, 'icon.ico'))
r = subprocess.run([WINDRES, '-O', 'coff', 'res.rc', '-o', 'res.o'],
                   cwd=临时, capture_output=True, encoding='utf-8', errors='replace')
if r.returncode != 0 or not os.path.exists(os.path.join(临时, 'res.o')):
    print('windres 失败：', (r.stderr or r.stdout)[:1500])
    sys.exit(1)
print('资源编译 OK')

# 4) gcc 链接（输出名必须 ASCII，再改名——Git Bash 传中文输出名会产乱码文件）
临时exe = os.path.join(临时, 'installer_tmp.exe')
if os.path.exists(临时exe):
    os.remove(临时exe)
cmd = [GCC, '-O2', '-std=gnu99', '-w', '-municode', '-mwindows', '-finput-charset=UTF-8',
       os.path.join(这里, '安装器.c'), os.path.join(临时, 'res.o'), '-o', 临时exe,
       '-lws2_32', '-lole32', '-loleaut32', '-lcomctl32', '-lshell32',
       '-luser32', '-lgdi32', '-ladvapi32', '-luuid']
r = subprocess.run(cmd, capture_output=True, encoding='utf-8', errors='replace')
if r.returncode != 0 or not os.path.exists(临时exe):
    print('gcc 失败：', (r.stderr or r.stdout)[:2000])
    sys.exit(1)
print('安装器链接 OK')

if os.path.exists(输出):
    os.remove(输出)
shutil.move(临时exe, 输出)
print('✅ 单文件安装程序：%s（%.1f MB，内含 %d 个文件）'
      % (输出, os.path.getsize(输出) / 1048576, len(文件们)))
shutil.rmtree(临时, ignore_errors=True)
