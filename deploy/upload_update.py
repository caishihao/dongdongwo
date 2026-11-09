#!/usr/bin/env python3
"""BongoCat 部署辅助（paramiko）：SFTP 上传 + 远程执行，凭据走 deploy/ssh.local.json。

用法（scripts/publish-release.ps1 与部署流程共用）：
  python deploy/upload_update.py --config deploy/ssh.local.json --put <本地> <远程> [--put ...]
  python deploy/upload_update.py --config deploy/ssh.local.json --run "<shell 命令>"
"""
import json
import sys

import paramiko


def load_config(path):
    with open(path, "r", encoding="utf-8") as f:
        cfg = json.load(f)
    return cfg["host"], int(cfg.get("port", 22)), cfg["user"], cfg["password"]


def main():
    args = sys.argv[1:]
    config_path = None
    puts = []  # (local, remote)
    runs = []
    i = 0
    while i < len(args):
        if args[i] == "--config":
            config_path = args[i + 1]
            i += 2
        elif args[i] == "--put":
            puts.append((args[i + 1], args[i + 2]))
            i += 3
        elif args[i] == "--run":
            runs.append(args[i + 1])
            i += 2
        else:
            print(f"未知参数：{args[i]}", file=sys.stderr)
            return 1

    if config_path is None:
        print("缺少 --config", file=sys.stderr)
        return 1

    host, port, user, password = load_config(config_path)
    client = paramiko.SSHClient()
    client.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    client.connect(host, port=port, username=user, password=password, timeout=25,
                   allow_agent=False, look_for_keys=False)
    try:
        if puts:
            sftp = client.open_sftp()
            # 确保远程目录存在（一级 mkdir -p 语义）
            for _, remote in puts:
                parts = remote.rsplit("/", 1)
                if len(parts) == 2 and parts[0]:
                    stdin, stdout, _ = client.exec_command(f"sudo mkdir -p {parts[0]} && sudo chown $(whoami) {parts[0]}")
                    if stdout.channel.recv_exit_status() != 0:
                        print(f"远程目录准备失败：{parts[0]}", file=sys.stderr)
                        return 1
            for local, remote in puts:
                print(f"上传 {local} → {host}:{remote}")
                sftp.put(local, remote)
                print(f"  完成（{__import__('os').path.getsize(local)} 字节）")
            sftp.close()

        for cmd in runs:
            print(f"执行：{cmd}")
            stdin, stdout, stderr = client.exec_command(cmd, timeout=120)
            out = stdout.read().decode("utf-8", "replace")
            err = stderr.read().decode("utf-8", "replace")
            rc = stdout.channel.recv_exit_status()
            if out.strip():
                print(out.rstrip())
            if err.strip():
                print("--- STDERR ---")
                print(err.rstrip())
            if rc != 0:
                print(f"命令退出码 {rc}", file=sys.stderr)
                return rc
        return 0
    finally:
        client.close()


if __name__ == "__main__":
    sys.exit(main())
