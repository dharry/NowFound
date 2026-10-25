// Detection of the ASP.NET Core shared runtime (Microsoft.AspNetCore.App) of a given major version ('10.').
// Included into the [Code] section of installer.iss (and of the check test script).
//
// - The .NET installers write their registry entries to the 32-bit view (WOW6432Node), even for x64,
//   so HKLM32 is checked as well as HKLM64. (HKLM alone is the 64-bit view in 64-bit install mode.)
// - The shared folder is checked too, because the registry entries are missing for zip / dotnet-install setups.

function HasAspNetCoreInRegistry(const RootKey: Integer; const MajorPrefix: String): Boolean;
var
  Versions: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(RootKey, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.AspNetCore.App', Versions) then
  begin
    for I := 0 to GetArrayLength(Versions) - 1 do
    begin
      if Pos(MajorPrefix, Versions[I]) = 1 then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function HasAspNetCoreInFolder(const DotNetDir, MajorPrefix: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(AddBackslash(DotNetDir) + 'shared\Microsoft.AspNetCore.App\' + MajorPrefix + '*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function IsAspNetCoreInstalled(const MajorPrefix: String): Boolean;
var
  Location: String;
begin
  Result := HasAspNetCoreInRegistry(HKLM32, MajorPrefix) or HasAspNetCoreInRegistry(HKLM64, MajorPrefix);

  if not Result then
  begin
    Result := HasAspNetCoreInFolder(ExpandConstant('{commonpf64}\dotnet'), MajorPrefix);
  end;

  // A custom install location is recorded by the .NET installers.
  if (not Result)
    and (RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location)
      or RegQueryStringValue(HKLM64, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Location)) then
  begin
    Result := HasAspNetCoreInFolder(Location, MajorPrefix);
  end;
end;

function IsAspNetCore10Installed: Boolean;
begin
  Result := IsAspNetCoreInstalled('10.');
end;
