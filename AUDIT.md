# Audit d'Auto Clic

Sources : le code 2.6.0 (`src/`), la v1 (`v1/`) et un journal d'usage réel du 17 au 23/11/2026 de la 2.5.1 (1 977 lignes).

## Bugs constatés (2.5.1)

| Priorité | Constat | Preuve | Correction proposée |
|---|---|---|---|
| **Corrigé (2.6.1)** | « JOUER » était reconnu dans « SIGNALER JOUEUR » : le programme cliquait sur le signalement d'un joueur | Capture à l'appui ; 16 clics autour de (244, 1040) | Texte court : une lettre différente est tolérée, plus une lettre en plus ou en moins (`src/TextFinder.cs`). « REJOUER » reste accepté |
| Haute | Autres clics peu sûrs (75 à 85 %) | 29 clics vers (1775, 928), quelques-uns isolés ; cible non vérifiée | Seuil à 88 % (presque tous les clics du bouton habituel sont à 90 % ou plus) |
| Moyenne | Alertes d'inactivité à chaque partie | 316 alertes ; 316 écarts de plus de 10 min entre détections (médiane 14,8, p90 18,3, max 40,9) | Délai de 25 min par défaut (10 dans `src/Storage.cs:255`) |
| Moyenne | Double action sur le même bouton | 71 détections à moins de 5 s de la précédente | Attendre la disparition du bouton avant de réagir |
| Moyenne | `CopyFromScreen` échoue : « Descripteur non valide » | 23/11 07:23, écran sans doute verrouillé | Suspendre puis reprendre, une seule ligne de journal |
| Basse | Alertes perdues si le réseau tombe | 5 alertes non envoyées | File d'attente et nouvel essai |
| À vérifier | Fermetures non propres | 9 démarrages, 4 « Programme quitté » | Journaliser l'arrêt de Windows et les exceptions non gérées |

## Timing

- **2.5.1** : environ 45,04 clics fixes / 10 min mesurés pour 45,37 attendus, soit −0,7 %
  (≈ 0,4 s par cycle de 52,9 s). Le calcul est approximatif (le journal compte aussi les
  détections), mais la cadence moyenne paraît correcte. Pour supprimer cette dérive :
  `_nextClick += p.Delay` au lieu de `Now + p.Delay` (`src/Engine.cs:472`), avec rattrapage
  limité. La macro tourne dans la même boucle que les clics (`src/Engine.cs:486`) et les retarde.
- **v1 (PowerShell)** :
  - les `Sleep` de la macro bloquent le minuteur de l'interface environ 450 ms toutes les
    10 s et 100 ms toutes les 3 s : clics en retard, F10 parfois ignoré ;
  - l'échéance est recalculée après l'action, donc chaque retard s'ajoute ;
  - le minuteur WinForms a une précision d'environ 16 ms ;
  - appui et relâchement sans délai, que certains jeux ne voient pas ;
  - aller-retour relatif de la souris soumis à l'accélération Windows.

## Sécurité

- Le nom de sujet ntfy doit être long et aléatoire : refuser les noms courts ou devinables
  quand les captures ou la télécommande sont activées.
- Ne jamais publier `boutons.json`, `captures/` ni `AutoClic.log` (voir `.gitignore`).
- Exe non signé : avertissement SmartScreen.

## Améliorations

1. Seuil par bouton : les notifications « a commencé à jouer à Fortnite » contiennent vraiment le mot (75 à 79 % de ressemblance). Pas de règle « mot entier » : le bouton principal est « REJOUER ».
2. Pause hors jeu activée par défaut (elle existe, désactivée dans la config observée).
3. Tests automatiques : texte, échéances, lecture de la config.
4. Compilation et publication par GitHub Actions.
5. Rotation du journal.
