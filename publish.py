from multiprocessing import Process
import argparse
import glob
import os
import os.path
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
    '*.toml'
]

TARGET_PROJ = 'LGSTrayUI'
PROJ_FILE = f'./{TARGET_PROJ}/{TARGET_PROJ}.csproj'
BUILD_PROPS_FILE = './Directory.Build.props'

# Parse version - check Directory.Build.props first, fall back to csproj
def find_version_prefix(*files):
    for f in files:
        if not os.path.exists(f):
            continue
        elements = ET.parse(f).getroot().findall('./PropertyGroup/VersionPrefix')
        if elements and elements[0].text:
            return elements[0].text, f
    return None, None

TARGET_VER, ver_source = find_version_prefix(BUILD_PROPS_FILE, PROJ_FILE)
if not TARGET_VER:
    raise ValueError(f"VersionPrefix not found in {BUILD_PROPS_FILE} or {PROJ_FILE}")

def file_list(zipFolder):
    for fileType in FILE_TYPES:
        # Fix: Add ** for proper recursive search
        pattern = os.path.join(zipFolder, '**', fileType)
        yield from glob.glob(pattern, recursive=True)

def create_zip(zipPath, zipFolder):
    with zipfile.ZipFile(zipPath, 'w', zipfile.ZIP_DEFLATED) as zip:
        for file in file_list(zipFolder):
            zip.write(file, os.path.basename(file))

class PublishHelper:
    def __init__(self, publish_root, no_zip):
        self.zip_threads = []

        self.publish_root = publish_root
        self.no_zip = no_zip

    def join(self):
        for p in self.zip_threads:
            p.join()

    def publish_profile(self, profile, zip_suffix):
        safe_ver = TARGET_VER.replace('.', '_')

        for proj in ["LGSTrayHID", "LGSTrayUI"]:
            subprocess.run(
                ["dotnet", "publish", f"{proj}/{proj}.csproj", f"/p:PublishProfile={profile}", f"/p:Version={TARGET_VER}"],
                shell=False,
                check=True  # Raise exception on failure
            )

        if self.no_zip:
            return

        zipName = f'Release_v{safe_ver}{zip_suffix}.zip'

        zipPath = os.path.join(self.publish_root, "..", zipName)
        zipFolder = os.path.join(self.publish_root, profile)

        print("\n---")
        print(f"Zipping {profile} ...")
        p = Process(target=create_zip, args=(zipPath, zipFolder))
        p.start()
        self.zip_threads.append(p)
        print("---")

def main(no_zip, version_suffix):
    global TARGET_VER
    TARGET_VER += version_suffix

    publish_root = os.path.join('./bin/Release/Publish/win-x64')

    helper = PublishHelper(publish_root, no_zip)
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

    args = parser.parse_args()

    main(**vars(args))
    print("\nPackaging done.")