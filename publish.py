from multiprocessing import Process
import argparse
import glob
import os
import os.path
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile

PUB_PROFILES = [
    ('Standalone', '-standalone'),
    ('Framedep', '')
]

FILE_TYPES = [
    '*.exe',
    '*.pdb',
    '*.dll',
    '*.toml',
    '*.json'
]

TARGET_PROJ = 'LGSTrayUI'
PROJ_FILE = f'./{TARGET_PROJ}/{TARGET_PROJ}.csproj'
TARGET_VER = ET.parse(PROJ_FILE).getroot() \
                .findall('./PropertyGroup/VersionPrefix')[0].text

def file_list(zipFolder):
    for fileType in FILE_TYPES:
        yield from glob.glob(os.path.join(zipFolder, fileType), recursive=True)

def create_zip(zipPath, zipFolder):
    with zipfile.ZipFile(zipPath, 'w', zipfile.ZIP_DEFLATED) as zip:
        for file in file_list(zipFolder):
            zip.write(file, os.path.basename(file))

def find_dotnet(dotnet_override):
    candidates = [
        dotnet_override,
        os.path.join(os.environ.get('ProgramFiles', ''), 'dotnet', 'dotnet.exe'),
        shutil.which('dotnet')
    ]

    for candidate in filter(None, candidates):
        result = subprocess.run(
            [candidate, '--list-sdks'],
            capture_output=True,
            text=True,
            shell=False
        )
        if result.returncode == 0 and result.stdout.strip():
            return candidate

    raise RuntimeError('A .NET SDK was not found. Install .NET 8 or pass --dotnet.')


class PublishHelper:
    def __init__(self, publish_root, no_zip, dotnet):
        self.zip_threads = []

        self.publish_root = Path(publish_root).resolve()
        self.no_zip = no_zip
        self.dotnet = dotnet

    def join(self):
        for p in self.zip_threads:
            p.join()

    def publish_profile(self, profile, zip_suffix):
        safe_ver = TARGET_VER.replace('.', '_')
        staging_root = self.publish_root / '.staging' / profile
        output_root = self.publish_root / profile

        if staging_root.exists():
            shutil.rmtree(staging_root)
        if output_root.exists():
            shutil.rmtree(output_root)

        for proj in ["LGSTrayHID", "LGSTrayUI"]:
            project_output = staging_root / proj
            subprocess.run(
                [
                    self.dotnet,
                    "publish",
                    f"{proj}/{proj}.csproj",
                    f"/p:PublishProfile={profile}",
                    f"/p:PublishDir={project_output}{os.sep}",
                    f"/p:Version={TARGET_VER}"
                ],
                check=True,
                shell=False
            )

        output_root.mkdir(parents=True, exist_ok=True)
        for proj in ["LGSTrayUI", "LGSTrayHID"]:
            shutil.copytree(staging_root / proj, output_root, dirs_exist_ok=True)

        shutil.rmtree(staging_root)

        if self.no_zip:
            return

        zipName = f'Release_v{safe_ver}{zip_suffix}.zip'

        zipPath = self.publish_root.parent / zipName
        zipFolder = self.publish_root / profile

        print("\n---")
        print(f"Zipping {profile} ...")
        p = Process(target=create_zip, args=(zipPath, zipFolder))
        p.start()
        self.zip_threads.append(p)
        print("---")

def main(no_zip, version_suffix, dotnet):
    global TARGET_VER
    TARGET_VER += version_suffix

    publish_root = os.path.join('./bin/Release/Publish/win-x64')

    helper = PublishHelper(publish_root, no_zip, find_dotnet(dotnet))
    for profile, zip_suffix in PUB_PROFILES:
        helper.publish_profile(profile, zip_suffix)

    helper.join()

if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        prog='publish.py',
        description='Publish helper'
    )
    parser.add_argument('--no-zip', action='store_true')
    parser.add_argument('--version-suffix', default='')
    parser.add_argument('--dotnet', help='Path to a dotnet executable with an installed SDK')

    args = parser.parse_args()

    main(**vars(args))
    print("\nPackaging done.")
