package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"regexp"
	"strings"
)

// applyModels writes the Settings choice of character models where each game reads it, just before the game starts.
// The returned environment is for LaunchEnv (none needed today).
//   - Windows: the client's config.ini [SYSTEM] Avatar (the client reads it at start; 2 = new models, else original).
//   - Mac, Wine app: GW_AVATAR in ~/Library/Application Support/<GWVariant>/settings.conf, because the app's launcher
//     rewrites config.ini from that file at every start (a value there wins over the environment).
//   - Mac, Native: avatar= in the app's Contents/Resources/settings.ini.
func applyModels(root, platform, engine, models string) ([]string, error) {
	if models != ModelsOriginal && models != ModelsNew {
		return nil, nil
	}
	switch {
	case platform != "mac":
		return nil, setIniValue(filepath.Join(root, "config.ini"), "SYSTEM", "Avatar", models)
	case engine == EngineNative:
		p := filepath.Join(root, NativeApp, "Contents", "Resources", "settings.ini")
		if _, err := os.Stat(p); err != nil {
			return nil, nil
		}
		return nil, setKeyLine(p, regexp.MustCompile(`^\s*avatar\s*=`), "avatar="+models)
	default:
		home, _ := os.UserHomeDir()
		variant := plistString(filepath.Join(root, "GodsWar.app", "Contents", "Info.plist"), "GWVariant")
		if variant == "" {
			variant = "GodsWar"
		}
		p := filepath.Join(home, "Library", "Application Support", variant, "settings.conf")
		if _, err := os.Stat(p); err != nil {
			// No settings file yet: the app's launcher.sh sets GW_AVATAR="1" before reading the file (so open --env
			// would be ignored) and writes a full file only when none exists. A file with just this line is read on
			// top of the app's defaults, which still apply to every other key.
			if err := os.MkdirAll(filepath.Dir(p), 0o755); err != nil {
				return nil, err
			}
			return nil, os.WriteFile(p, []byte("# GodsWar client settings. Hold Option when opening the app to edit.\n# 1 = original character models; 2 = new character models\nGW_AVATAR=\""+models+"\"\n"), 0o644)
		}
		return nil, setKeyLine(p, regexp.MustCompile(`^\s*GW_AVATAR\s*=`), `GW_AVATAR="`+models+`"`)
	}
}

// setKeyLine replaces the first line matching re with line (or appends it), keeping the rest of the file and its mode.
func setKeyLine(path string, re *regexp.Regexp, line string) error {
	b, err := os.ReadFile(path)
	if err != nil {
		return err
	}
	st, _ := os.Stat(path)
	lines := strings.Split(string(b), "\n")
	done := false
	for i, l := range lines {
		if re.MatchString(l) {
			if strings.TrimSpace(l) == line {
				return nil
			}
			lines[i], done = line, true
			break
		}
	}
	if !done {
		if n := len(lines); n > 0 && lines[n-1] == "" {
			lines = append(lines[:n-1], line, "")
		} else {
			lines = append(lines, line)
		}
	}
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, []byte(strings.Join(lines, "\n")), st.Mode().Perm()); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

// plistString reads a string key from an XML Info.plist ("" when missing).
func plistString(path, key string) string {
	b, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	m := regexp.MustCompile(`<key>` + regexp.QuoteMeta(key) + `</key>\s*<string>([^<]*)</string>`).FindSubmatch(b)
	if m == nil {
		return ""
	}
	return string(m[1])
}

// LaunchEnv starts the game like Launch, with extra environment for the Mac Wine app.
func LaunchEnv(root, platform, engine string, env []string) error {
	if platform != "mac" || len(env) == 0 {
		return Launch(root, platform, engine)
	}
	args := []string{}
	for _, e := range env {
		args = append(args, "--env", e)
	}
	app := "GodsWar.app"
	if engine == EngineNative {
		app = NativeApp
	}
	return exec.Command("/usr/bin/open", append(args, filepath.Join(root, app))...).Start()
}

// installedModels reads the models the game will use now (see applyModels for where each one keeps it).
func installedModels(st State, platform, engine string) string {
	val := ""
	def := ModelsOriginal
	switch {
	case platform != "mac":
		if st.ClientRoot != "" {
			val = iniValue(filepath.Join(st.ClientRoot, "config.ini"), "SYSTEM", "Avatar")
		}
	case engine == EngineNative:
		def = ModelsNew // Native's default
		if st.NativeRoot != "" {
			val = keyValue(filepath.Join(st.NativeRoot, NativeApp, "Contents", "Resources", "settings.ini"), "avatar")
		}
	default:
		home, _ := os.UserHomeDir()
		variant := "GodsWar"
		if st.ClientRoot != "" {
			if v := plistString(filepath.Join(st.ClientRoot, "GodsWar.app", "Contents", "Info.plist"), "GWVariant"); v != "" {
				variant = v
			}
		}
		val = strings.Trim(keyValue(filepath.Join(home, "Library", "Application Support", variant, "settings.conf"), "GW_AVATAR"), `"'`)
	}
	if val == ModelsNew || val == ModelsOriginal {
		if val == ModelsNew {
			return ModelsNew
		}
		return ModelsOriginal
	}
	if val != "" { // the client treats anything but 2 as the original models
		return ModelsOriginal
	}
	return def
}

// keyValue reads key=value from a flat settings file ("" when missing).
func keyValue(path, key string) string {
	b, err := os.ReadFile(path)
	if err != nil {
		return ""
	}
	for _, l := range strings.Split(string(b), "\n") {
		if k, v, ok := strings.Cut(strings.TrimSpace(l), "="); ok && strings.TrimSpace(k) == key {
			return strings.TrimSpace(v)
		}
	}
	return ""
}
