using System.Security.Cryptography;
using SoluPulse.Platform.Windows;

// Outil de Solutech : signe les listes de détection publiées dans data/. La clé privée ne doit jamais
// entrer dans le dépôt.
//   generer <dossier>              crée la paire de clés (clé privée dans le dossier, clé publique affichée)
//   exporter <listes.json>         écrit la liste intégrée à SoluPulse, point de départ d'une nouvelle liste
//   signer <listes.json> <clé.pem> écrit listes.json.sig à côté de la liste
//   verifier <listes.json>         vérifie la signature avec la clé publique intégrée à SoluPulse
switch (args)
{
    case ["generer", var folder]:
        Directory.CreateDirectory(folder);
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            var privatePath = Path.Combine(folder, "cle-signature-listes.pem");
            if (File.Exists(privatePath))
            {
                Console.Error.WriteLine($"Une clé existe déjà : {privatePath}");
                return 1;
            }

            File.WriteAllText(privatePath, key.ExportPkcs8PrivateKeyPem());
            Console.WriteLine($"Clé privée : {privatePath} (à sauvegarder hors de ce PC, ne jamais la publier)");
            Console.WriteLine(key.ExportSubjectPublicKeyInfoPem());
        }

        return 0;

    case ["exporter", var path]:
        File.WriteAllText(path, ThreatLists.Serialize(ThreatLists.Builtin) + "\n");
        Console.WriteLine($"Liste écrite : {path}");
        return 0;

    case ["signer", var path, var keyPath]:
        using (var key = ECDsa.Create())
        {
            key.ImportFromPem(File.ReadAllText(keyPath));
            var signature = key.SignData(File.ReadAllBytes(path), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            File.WriteAllText(path + ".sig", Convert.ToBase64String(signature) + "\n");
            Console.WriteLine($"Signature écrite : {path}.sig");
        }

        return 0;

    case ["verifier", var path]:
        var valid = ThreatLists.Verify(File.ReadAllBytes(path), File.ReadAllText(path + ".sig"), ThreatLists.PublicKeyPem);
        Console.WriteLine(valid ? "Signature valide." : "Signature INVALIDE.");
        return valid ? 0 : 1;

    default:
        Console.Error.WriteLine("Usage : generer <dossier> | exporter <listes.json> | signer <listes.json> <clé.pem> | verifier <listes.json>");
        return 2;
}
