create table hm_imapexpunged (
	expungedaccountid int not null,
	expungedfolderid int not null,
	expungeduid bigint not null,
	expungedmodseq bigint not null
)

CREATE INDEX idx_hm_imapexpunged ON hm_imapexpunged (expungedfolderid, expungedmodseq)

update hm_dbversion set value = 6003
