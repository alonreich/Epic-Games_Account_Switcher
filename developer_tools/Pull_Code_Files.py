import os
import shutil
from pathlib import Path

def is_binary(file_path):
    try:
        with open(file_path, 'rb') as f:
            chunk = f.read(1024)
            if not chunk:
                return False
            if b'\x00' in chunk:
                return True
            text_chars = bytearray({7, 8, 9, 10, 12, 13, 27} | set(range(0x20, 0x100)) - {0x7f})
            non_text = chunk.translate(None, text_chars)
            if len(non_text) / len(chunk) > 0.30:
                return True
    except Exception:
        return True
    return False

def run_aggregator():
    project_root = Path(__file__).resolve().parent.parent
    download_dir = Path(os.environ['USERPROFILE']) / "Downloads" / "Epic-Games_Account_Switcher"

    if download_dir.exists():
        shutil.rmtree(download_dir)
    download_dir.mkdir(parents=True, exist_ok=True)

    ignored_dirs = {'.git', 'bin', 'obj', '.vs', '.idea', 'node_modules', 'developer_tools'}

    for root, dirs, files in os.walk(project_root):
        dirs[:] = [d for d in dirs if d not in ignored_dirs]
        current_dir = Path(root)
        
        group_name = "root" if current_dir == project_root else current_dir.name
        output_file_path = download_dir / f"{group_name}.txt"

        for filename in files:
            file_path = current_dir / filename
            
            if is_binary(file_path):
                continue

            try:
                content = file_path.read_text(encoding='utf-8', errors='ignore')
                formatted_entry = f"\n\n\n{file_path}:\n`\n{content}\n`"
                
                with open(output_file_path, 'a', encoding='utf-8') as f_out:
                    f_out.write(formatted_entry)

            except Exception:
                continue

if __name__ == "__main__":
    run_aggregator()