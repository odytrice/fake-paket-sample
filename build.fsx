#r "paket: groupref Build"
#load ".fake/build.fsx/intellisense.fsx"

//open Paket.Core

open Fake.Core
open Fake.DotNet
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Core.TargetOperators
open System.Linq

open System.IO
open System

open Paket.Domain
open Fake.IO

let Project = {| PackageName = "App.Library"; ProjectFile = "App.Library" </> "App.Library.fsproj" |}

let UpdatePackageVersion (this: Paket.DependenciesFile) groupName packageName (version: string) =

    let tryMatchPackageLine packageNamePredicate (line : string) =
        let tokens = line.Split([|' '|], StringSplitOptions.RemoveEmptyEntries) |> Array.map (fun s -> s.ToLowerInvariant().Trim())
        match List.ofArray tokens with
        | "nuget"::packageName::_ when packageNamePredicate packageName -> Some packageName
        | _ -> None

    let isPackageLine name line = tryMatchPackageLine ((=) name) line |> Option.isSome

    if this.HasPackage(groupName,packageName) then
        let vr = Paket.DependenciesFileParser.parseVersionString version
        let newLines =
            this.Lines
            |> Array.map (fun line ->
                let name = packageName.CompareString
                if isPackageLine name line then
                    let p = this.GetPackage(groupName,packageName)
                    let packageLineString = Paket.DependenciesFileSerializer.packageString Paket.Requirements.PackageRequirementKind.Package packageName vr.VersionRequirement vr.ResolverStrategy p.Settings
                    String(' ', 4) + packageLineString
                else line)

        Paket.DependenciesFile(Paket.DependenciesFileParser.parseDependenciesFile this.FileName false newLines)
    else
        this

let getFileVersion (dependenciesFile: Paket.DependenciesFile) (group: GroupName) (packageName: PackageName) =
    let findPackage = dependenciesFile.GetGroup(group).Packages |> List.tryFind (fun p -> p.Name = packageName)
    match findPackage with
    | Some package ->
        match package.VersionRequirement with
        | Paket.VersionRequirement(Paket.Specific ver,_) ->
            Some ver
        | _ -> failwith "Could not Find Version"
    | None -> None

let getVersion parameters =
    let arguments = parameters.Context.Arguments
    match arguments with
    | [] -> failwithf "Please specify the versionnumber using: --version VERSIONNUMBER"
    | ["--version"; version] -> Fake.Core.SemVer.parse version
    | other -> failwithf "Unrecognized options! Please only pass the version number. You gave me: %A" other




Target.create "Pack" (fun parameters ->
    let packHandler (version : SemVerInfo) =
        Shell.mkdir (__SOURCE_DIRECTORY__ </> "nuget")
        Paket.pack
            (fun settings ->
                printfn "Output Path: %s" (__SOURCE_DIRECTORY__ </> "nuget")
                { settings with
                    TemplateFile = IO.Path.Combine(Path.getDirectory(Project.ProjectFile), "paket.template")
                    Version = version.AsString
                    OutputPath = (__SOURCE_DIRECTORY__ </> "nuget") // we build into nuget so we can easily upload everything in there with one command
                    BuildConfig =  "Release" }
                )
    let parsedVersion = getVersion parameters
    packHandler parsedVersion)


let pushToGitHub packageName =

    let value = Environment.GetEnvironmentVariable("GITHUB_PUSH_TOKEN")
    let message = "You need to set the environment variable 'GITHUB_PUSH_TOKEN' with a GitHub Access Token https://help.github.com/en/github/authenticating-to-github/creating-a-personal-access-token-for-the-command-line"
    if String.IsNullOrEmpty(value) then failwith message

    //Get All versions for specific Package e.g. nuget/EdelwiessData.Core.0.0.1.nupkg
    let pattern = sprintf "%s*" packageName
    let files = IO.Directory.GetFiles("nuget", pattern)

    let push file =
        let nugetPush =
            if Environment.isWindows then
                [ "push"; "-Source"; "GitHub"; "-ConfigFile"; ".nuget/Nuget/Nuget.config"; file ]
                |> CreateProcess.fromRawCommand ".nuget/nuget.exe"
                |> CreateProcess.redirectOutput
                |> Proc.run
            else
                [ ".nuget/nuget.exe"; "push"; "-Source"; "GitHub"; "-ConfigFile"; ".nuget/Nuget/Nuget.config"; file ]
                |> CreateProcess.fromRawCommand "mono"
                |> CreateProcess.redirectOutput
                |> Proc.run


        if nugetPush.ExitCode > 0 then
            failwith (sprintf "Could not push Package: %s" nugetPush.Result.Error)

    files |> Seq.iter (push)

Target.create "Push" (fun _ -> pushToGitHub Project.PackageName)


Target.create "UpdatePackage" (fun _ ->
    let path = Path.Combine(Directory.GetCurrentDirectory(), "paket.dependencies")
    let dependenciesFile = Paket.DependenciesFile.ReadFromFile(path)

    let group = GroupName "App"
    let packageName = PackageName "App.Library"


    //Increment Version Number
    packageName
    |> getFileVersion dependenciesFile group
    |> Option.map (fun version -> { version with Patch = version.Patch + 1u; Original = None })
    |> Option.map (fun v -> UpdatePackageVersion dependenciesFile group packageName (v.ToString()))
    |> Option.iter (fun file -> file.Save())

    Paket.Dependencies.Locate().Install(force = false)
)



Target.create "Clean" (fun _ ->
    !! "src/**/bin"
    ++ "src/**/obj"
    |> Shell.cleanDirs
)

Target.create "Build" (fun _ ->
    !! "src/**/*.*proj"
    |> Seq.iter (DotNet.build id)
)

Target.create "All" ignore

"Clean"
  ==> "Build"
  ==> "Pack"
  ==> "Push"
  ==> "Update"
  ==> "All"

Target.runOrDefault "All"
