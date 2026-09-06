# mTiles on Arch Linux

Two ways in. The first gives you `pacman -Syu`; the second gives you a package without adding
anything to your system's configuration.

## Add the repository (recommended — updates come with the system)

Import the signing key, then add the repository:

```bash
curl -fsSL https://github.com/b-y-t-e/mTiles/releases/download/arch-repo/mtiles-repo.gpg \
  | sudo pacman-key --add -
sudo pacman-key --lsign-key C425FE40B63D0BA5B3C193A4FEC939C56E4FF630
```

Signing the key locally (`--lsign-key`) is what tells pacman you trust it on this machine — having
the key is deliberately not enough on its own. The fingerprint above is also printed by
`pacman-key --add` and by the Arch repository workflow, so you can check it against both.

Then append to `/etc/pacman.conf`:

```ini
[mtiles]
SigLevel = Required
Server = https://github.com/b-y-t-e/mTiles/releases/download/arch-repo
```

```bash
sudo pacman -Sy mtiles-bin
```

From then on `pacman -Syu` upgrades mTiles along with everything else.

### On Omarchy

Omarchy installs a pacman hook that refuses a direct upgrade and points at `omarchy update`, which
is the right path for the system as a whole. To upgrade this one package without going through it,
use the escape the hook itself names:

```bash
sudo env OMARCHY_ALLOW_DIRECT_PACMAN=1 pacman -Syu
```

That is not a partial upgrade as long as the transaction really is one package — pacman prints the
list before asking, so read it. No AUR helper involved:
this is a real repository, so plain pacman sees it.

`SigLevel = Required` is not decoration. It is the whole reason the key exists — it is what
stops a package that did not come from this build from installing. Do not replace it with
`Optional TrustAll`.

## Or build it yourself

```bash
git clone https://github.com/b-y-t-e/mTiles.git
cd mTiles/packaging/arch
makepkg -si
```

The `PKGBUILD` here is rendered and committed by CI from the newest release, so it always matches
what the repository is serving. Updates are manual: `git pull && makepkg -si`.

It appears with the first release built after this packaging was added — the tarball it sources
did not exist in earlier releases, so there is nothing honest to commit before then.

## What gets installed

```
/opt/mtiles/                                        the self-contained .NET publish
/usr/bin/mtiles                                     wrapper
/usr/share/applications/mtiles.desktop
/usr/share/icons/hicolor/256x256/apps/mtiles.png
/usr/share/licenses/mtiles-bin/LICENSE
```

The built-in updater does nothing in this package, and that is deliberate: `/opt/mtiles` belongs
to pacman, and an application writing over its own files there would leave the package database
describing something that is no longer on disk. Updates come from the repository.

## AUR

`mtiles-bin` is not in the AUR yet — new AUR account registration has been closed since late
August 2026 following an incident with malicious packages. The `PKGBUILD` here is the one that
will be pushed there when it reopens; nothing about it will need to change.

## Setting up signing (maintainer)

The workflow needs two secrets. Generate a key that is used for nothing else:

```bash
gpg --batch --passphrase ''     --quick-generate-key "mTiles repository <andrzej.bol@gmail.com>" rsa4096 sign never
gpg --armor --export-secret-keys "mTiles repository"   # -> secret ARCH_REPO_GPG_KEY
gpg --fingerprint "mTiles repository"                  # -> the fingerprint users lsign
```

Add the whole exported block as `ARCH_REPO_GPG_KEY` in the repository's Actions secrets. That is
the only secret needed.

No passphrase, and that is a decision rather than a shortcut: the key already lives in a GitHub
secret, so a passphrase would sit in a second secret beside it and protect nothing, while costing
a gpg-agent pinentry dance in the container that `repo-add` offers no way to pass a loopback mode
through. What the key is worth guarding against is somebody publishing packages as you, and the
answer to that is the secret store, not a string next to it.

The workflow refuses to publish an unsigned repository rather than falling back to one users would
have to disable verification for.
